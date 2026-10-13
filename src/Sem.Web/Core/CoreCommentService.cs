using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Core;
public sealed record CommentView(string Id,string UserId,string DisplayName,string Body,string CreatedAt);
public sealed record CommentCursor(string At,string Id);
public sealed record CommentPlan(string EventId,string Title,long EventVersion,bool CanManage,bool CanPost,string? ClosesAt,CommentView[] Comments,CommentCursor? Next);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CommentEdit(string RequestId,long EventVersion,string Body);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CommentDeleteEdit(string RequestId,string CommentId);
public sealed record CommentMutationResult(string RequestId,string CommentId,bool Replayed=false);
public static class CommentPolicy {
 public static DateTimeOffset? EndExclusive(string scheduled){
  if(!DateTimeOffset.TryParse(scheduled,CultureInfo.InvariantCulture,DateTimeStyles.None,out var instant))return null;
  try{return new DateTimeOffset(instant.ToOffset(TimeSpan.FromHours(9)).Date.AddDays(1),TimeSpan.FromHours(9)).ToUniversalTime();}catch(ArgumentOutOfRangeException){return null;}
 }
 public static bool Open(string scheduled,bool manager,DateTimeOffset now)=>manager||EndExclusive(scheduled) is DateTimeOffset end&&now<end;
}
public sealed partial class CoreFoundationService {
 private static string CommentBody(string? input){var value=Text(input,65536,"コメント",true);if(value.EnumerateRunes().Count()<3)throw new ValidationException("コメントは前後の空白を除いて3文字以上で入力してください。");return value;}
 private void RequireCommentOpen(SessionEvent e,bool manager){if(!CommentPolicy.Open(e.ScheduledDate,manager,clock.GetUtcNow()))throw new ValidationException("コメント受付は開催日の終了までです（日本時間）。");}
 private void AddComment(ApplicationDbContext db,SessionEvent e,string actorId,string commentId,string body,bool manager){var now=clock.GetUtcNow();if(!CommentPolicy.Open(e.ScheduledDate,manager,now))throw new ValidationException("コメント受付は開催日の終了までです（日本時間）。");db.EventComments.Add(new(){Id=commentId,EventId=e.Id,UserId=actorId,Body=body,CreatedAt=Utc(now)});Audit(db,actorId,"CommentCreated",commentId);}
 public async Task<CommentPlan> ReadCommentsAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default,CommentCursor? before=null){
  if(before is not null&&(!Guid.TryParse(before.Id,out _)||!DateTimeOffset.TryParseExact(before.At,"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out _)))throw new ValidationException("一覧の続きを確認してください。");
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var(e,_,_,manager)=await ParticipantEvent(db,principal,id,ct);var rows=db.EventComments.AsNoTracking().Where(x=>x.EventId==id);
  if(before is not null)rows=rows.Where(x=>string.Compare(x.CreatedAt,before.At)<0||x.CreatedAt==before.At&&string.Compare(x.Id,before.Id)<0);
  var comments=await(from c in rows join u in db.Users on c.UserId equals u.Id orderby c.CreatedAt descending,c.Id descending select new CommentView(c.Id,c.UserId,u.DisplayName,c.Body,c.CreatedAt)).Take(101).ToArrayAsync(ct);
  var visible=comments.Take(100).ToArray();var next=comments.Length>100?new CommentCursor(visible[^1].CreatedAt,visible[^1].Id):null;var end=CommentPolicy.EndExclusive(e.ScheduledDate);
  await tx.CommitAsync(ct);return new(e.Id,e.Title,e.RowVersion,manager,CommentPolicy.Open(e.ScheduledDate,manager,clock.GetUtcNow()),end is null?null:Utc(end.Value),visible,next);
 }
 public async Task<CommentMutationResult> PostCommentAsync(ClaimsPrincipal principal,string id,CommentEdit edit,CancellationToken ct=default){
  if(!Guid.TryParse(edit.RequestId,out _)||edit.EventVersion<0)throw new ValidationException("操作Idとイベント版を確認してください。");var body=CommentBody(edit.Body);var hash=Sem.Web.Runtime.SemEndpoints.Hash(JsonSerializer.Serialize(edit));
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var(e,actor,_,manager)=await ParticipantEvent(db,principal,id,ct);
  var old=await db.CommentOperations.SingleOrDefaultAsync(x=>x.Id==edit.RequestId,ct);if(old is not null){if(old.EventId!=id||old.ActorId!=actor.Id||old.Kind!="Post"||old.RequestHash!=hash)throw new ValidationException("操作Idは再利用できません。");return new(edit.RequestId,old.CommentId,true);}
  if(e.RowVersion!=edit.EventVersion)throw new CoreConflictException();RequireCommentOpen(e,manager);
  // A combined participation operation owns its comment ID; standalone requests cannot collide.
  if(await db.EventComments.AnyAsync(x=>x.Id==edit.RequestId,ct)||await db.RegistrationOperations.AnyAsync(x=>x.Id==edit.RequestId,ct))throw new ValidationException("操作Idは再利用できません。");
  AddComment(db,e,actor.Id,edit.RequestId,body,manager);db.CommentOperations.Add(new(){Id=edit.RequestId,EventId=id,ActorId=actor.Id,Kind="Post",CommentId=edit.RequestId,RequestHash=hash,At=Utc(clock.GetUtcNow())});await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(edit.RequestId,edit.RequestId);
 }
 public async Task<CommentMutationResult> DeleteCommentAsync(ClaimsPrincipal principal,string id,CommentDeleteEdit edit,CancellationToken ct=default){
  if(!Guid.TryParse(edit.RequestId,out _)||!Guid.TryParse(edit.CommentId,out _))throw new ValidationException("操作Idとコメントを確認してください。");var hash=Sem.Web.Runtime.SemEndpoints.Hash(JsonSerializer.Serialize(edit));
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var(e,actor,_,manager)=await ParticipantEvent(db,principal,id,ct);if(!manager)throw new UnauthorizedAccessException();
  var old=await db.CommentOperations.SingleOrDefaultAsync(x=>x.Id==edit.RequestId,ct);if(old is not null){if(old.EventId!=id||old.ActorId!=actor.Id||old.Kind!="Delete"||old.RequestHash!=hash)throw new ValidationException("操作Idは再利用できません。");return new(edit.RequestId,old.CommentId,true);}
  if(await db.RegistrationOperations.AnyAsync(x=>x.Id==edit.RequestId,ct))throw new ValidationException("操作Idは再利用できません。");
  var comment=await db.EventComments.SingleOrDefaultAsync(x=>x.Id==edit.CommentId&&x.EventId==id,ct)??throw new CoreConflictException();
  db.EventComments.Remove(comment);db.CommentOperations.Add(new(){Id=edit.RequestId,EventId=e.Id,ActorId=actor.Id,Kind="Delete",CommentId=comment.Id,RequestHash=hash,At=Utc(clock.GetUtcNow())});Audit(db,actor.Id,"CommentDeleted",comment.Id);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(edit.RequestId,edit.CommentId);
 }
}
