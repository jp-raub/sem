using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
public sealed record PublicRespondent(string UserId,string DisplayName);
public sealed record PublicAnswerGroup(string? Value,int Count,PublicRespondent[] Respondents);
public sealed record PublicQuestionSummary(string Id,string Text,string Type,bool PublicText,int AnsweredCount,PublicAnswerGroup[] Groups);
public sealed record PublicAnswerPlan(string EventId,string Title,long RowVersion,PublicQuestionSummary[] Questions);
public sealed partial class CoreFoundationService {
 public async Task<PublicAnswerPlan> ReadPublicAnswersAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);
  // One read snapshot: publication settings, permission and projected answers cannot mix revisions.
  await using var tx=await db.Database.BeginTransactionAsync(ct);
  var(e,_,_,_)=await ParticipantEvent(db,principal,id,ct);
  var questions=await db.EventQuestions.AsNoTracking().Where(q=>q.EventId==id&&q.DeletedAt==null&&!q.Disabled&&q.Public&&(q.Type=="YesNo"||q.Type=="Text"))
   .OrderBy(q=>q.SortOrder).ThenBy(q=>q.Id).Select(q=>new{q.Id,q.Text,q.Type,q.PublicText}).ToArrayAsync(ct);
  var summaries=new List<PublicQuestionSummary>();
  foreach(var q in questions){
   var answers=db.EventQuestionAnswers.AsNoTracking().Where(a=>a.QuestionId==q.Id&&a.Value!=null&&a.Value.Replace(" ","").Replace("\t","").Replace("\r","").Replace("\n","").Replace("\v","").Replace("\f","").Replace("\u0085","").Replace("\u00a0","").Replace("\u1680","").Replace("\u2000","").Replace("\u2001","").Replace("\u2002","").Replace("\u2003","").Replace("\u2004","").Replace("\u2005","").Replace("\u2006","").Replace("\u2007","").Replace("\u2008","").Replace("\u2009","").Replace("\u200a","").Replace("\u2028","").Replace("\u2029","").Replace("\u202f","").Replace("\u205f","").Replace("\u3000","")!="");
   var expose=q.Type=="YesNo"||q.PublicText;
   // CASE is translated to SQL. Private text never enters the result projection or DTO.
   var rows=await(from a in answers join u in db.Users on a.UserId equals u.Id
    select new{u.Id,u.DisplayName,Value=expose?a.Value:null}).ToArrayAsync(ct);
   if(q.Type=="YesNo")rows=rows.Where(a=>a.Value?.Trim() is "YES" or "NO").ToArray();
   PublicRespondent[] People(IEnumerable<(string Id,string Name)> people)=>people.OrderBy(x=>x.Name,StringComparer.Ordinal).ThenBy(x=>x.Id,StringComparer.Ordinal).Select(x=>new PublicRespondent(x.Id,x.Name)).ToArray();
   PublicAnswerGroup Group(string? value,IEnumerable<(string Id,string Name)> people){var p=People(people);return new(value,p.Length,p);}
   PublicAnswerGroup[] groups;
   if(q.Type=="YesNo")groups=new[]{"YES","NO"}.Select(value=>Group(value,rows.Where(a=>a.Value!.Trim()==value).Select(a=>(a.Id,a.DisplayName)))).ToArray();
   else if(!q.PublicText)groups=[Group(null,rows.Select(a=>(a.Id,a.DisplayName)))];
   else groups=rows.GroupBy(a=>a.Value!.Trim(),StringComparer.Ordinal).OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>Group(g.Key,g.Select(a=>(a.Id,a.DisplayName)))).ToArray();
   summaries.Add(new(q.Id,q.Text,q.Type,q.Type=="Text"&&q.PublicText,rows.Length,groups));
  }
  await tx.CommitAsync(ct);
  return new(e.Id,e.Title,e.RowVersion,summaries.ToArray());
 }
}
