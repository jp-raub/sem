using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
public sealed record EventQuestionView(string Id,string Text,string Type,bool Required,bool Disabled,bool Public,bool PublicText,int AnswerCount);
public sealed record EventQuestionPlan(string EventId,string Title,long RowVersion,EventQuestionView[] Questions);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventQuestionEdit(string Id,string Text,string Type,bool Required=false,bool Disabled=false,bool Public=false,bool PublicText=false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventQuestionPlanEdit(long RowVersion,EventQuestionEdit[] Questions);
public sealed partial class CoreFoundationService {
 public async Task<EventQuestionPlan> ReadQuestionsAsync(ClaimsPrincipal principal,string eventId,CancellationToken ct=default) {
  await using var db=await factory.CreateDbContextAsync(ct);var e=await Managed(db,principal,eventId,ct);
  var questions=await db.EventQuestions.Where(q=>q.EventId==e.Id&&q.DeletedAt==null).OrderBy(q=>q.SortOrder).ThenBy(q=>q.Id)
   .Select(q=>new EventQuestionView(q.Id,q.Text,q.Type,q.Required,q.Disabled,q.Public,q.PublicText,db.EventQuestionAnswers.Count(a=>a.QuestionId==q.Id))).ToArrayAsync(ct);
  return new(e.Id,e.Title,e.RowVersion,questions);
 }
 public async Task<EventQuestionPlan> SaveQuestionsAsync(ClaimsPrincipal principal,string eventId,EventQuestionPlanEdit edit,CancellationToken ct=default) {
  if(edit.RowVersion<0||edit.Questions is null||edit.Questions.Length>100||edit.Questions.Any(q=>q is null)||edit.Questions.Select(q=>q.Id).Distinct(StringComparer.Ordinal).Count()!=edit.Questions.Length)throw new ValidationException("設問の件数／識別子／版を確認してください。");
  var drafts=edit.Questions.Select(q=>{var text=Text(q.Text,200,"質問文",true);if(q.Type is not ("Text" or "YesNo")||q.PublicText&&(!q.Public||q.Type!="Text"))throw new ValidationException("回答形式／公開条件を確認してください。本文公開には自由記述と集計公開が必要です。");return q with{Text=text};}).ToArray();
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var e=await Managed(db,principal,eventId,ct);var actor=await Actor(db,principal,ct);if(e.RowVersion!=edit.RowVersion)throw new CoreConflictException();
  var rows=await db.EventQuestions.Where(q=>q.EventId==e.Id).ToListAsync(ct);
  foreach(var d in drafts){var row=rows.SingleOrDefault(q=>q.Id==d.Id);
   if(row is not null&&(row.DeletedAt is not null||row.Type!=d.Type))throw new ValidationException("作成済みの回答形式や削除済み設問は変更できません。");
   if(row is null&&(!Guid.TryParse(d.Id,out _)||await db.EventQuestions.AnyAsync(q=>q.Id==d.Id,ct)))throw new ValidationException("新規設問の識別子を確認してください。");
  }
  var removed=rows.Where(q=>q.DeletedAt==null&&!drafts.Any(d=>d.Id==q.Id)).ToArray();var ids=removed.Select(q=>q.Id).ToArray();
  if(await db.EventQuestionAnswers.AnyAsync(a=>ids.Contains(a.QuestionId),ct))throw new ValidationException("回答のある設問は削除できません。使用停止を利用してください。");
  foreach(var row in removed)row.DeletedAt=Utc(clock.GetUtcNow());var order=0;
  foreach(var d in drafts){var row=rows.SingleOrDefault(q=>q.Id==d.Id);if(row is null){row=new(){Id=d.Id,EventId=e.Id};db.EventQuestions.Add(row);}row.Text=d.Text;row.Type=d.Type;row.Required=d.Required;row.Disabled=d.Disabled;row.Public=d.Public;row.PublicText=d.PublicText;row.SortOrder=order++;}
  e.RowVersion++;Audit(db,actor.Id,"EventQuestionPlanSaved",e.Id);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await ReadQuestionsAsync(principal,eventId,ct);
 }
}
