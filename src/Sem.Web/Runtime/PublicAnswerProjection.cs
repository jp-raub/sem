using Microsoft.EntityFrameworkCore;
using Sem.Web.Core;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed record AnswerRespondent(string Id,string DisplayName);
public sealed record AnswerGroup(string? Value,int Count,AnswerRespondent[] Respondents);
public sealed record AnswerSummary(string Id,string Text,string Type,bool PublicText,int AnsweredCount,AnswerGroup[] Groups);
// EventDetailReaderの現認証・イベント認可済みread snapshotからだけ呼ぶ投影部品。独立公開APIにはしない。
public static class PublicAnswerProjection {
 public static async Task<AnswerSummary[]> ReadAsync(ApplicationDbContext db,string eventId,CancellationToken ct=default){
  var questions=await db.EventQuestions.AsNoTracking().Where(q=>q.EventId==eventId&&q.DeletedAt==null&&!q.Disabled&&q.Public&&(q.Type=="YesNo"||q.Type=="Text")).OrderBy(q=>q.SortOrder).ThenBy(q=>q.Id).Select(q=>new{q.Id,q.Text,q.Type,q.PublicText}).ToArrayAsync(ct);
  var result=new List<AnswerSummary>();
  foreach(var q in questions){
   // .NETのTrimと同じUnicode空白を除いた存在判定をSQLで行う。本文非公開では結果列へValueを含めない。
   var answers=db.EventQuestionAnswers.AsNoTracking().Where(a=>a.QuestionId==q.Id&&a.Value!=null&&a.Value.Replace(" ","").Replace("\t","").Replace("\r","").Replace("\n","").Replace("\v","").Replace("\f","").Replace("\u0085","").Replace("\u00a0","").Replace("\u1680","").Replace("\u2000","").Replace("\u2001","").Replace("\u2002","").Replace("\u2003","").Replace("\u2004","").Replace("\u2005","").Replace("\u2006","").Replace("\u2007","").Replace("\u2008","").Replace("\u2009","").Replace("\u200a","").Replace("\u2028","").Replace("\u2029","").Replace("\u202f","").Replace("\u205f","").Replace("\u3000","")!="");
   var people=from a in answers join u in db.Users.AsNoTracking() on a.UserId equals u.Id select new{u.Id,u.DisplayName,a.Value};
   AnswerGroup Group(string? value,IEnumerable<AnswerRespondent> rows){var p=rows.OrderBy(r=>r.DisplayName,StringComparer.Ordinal).ThenBy(r=>r.Id,StringComparer.Ordinal).ToArray();return new(value,p.Length,p);}
   AnswerGroup[] groups;int count;
   if(q.Type=="Text"&&!q.PublicText){var rows=await people.Select(p=>new AnswerRespondent(p.Id,p.DisplayName)).ToArrayAsync(ct);groups=[Group(null,rows)];count=rows.Length;}
   else{var rows=await people.ToArrayAsync(ct);if(q.Type=="YesNo")rows=rows.Where(r=>r.Value?.Trim() is "YES" or "NO").ToArray();count=rows.Length;
    groups=q.Type=="YesNo"?new[]{"YES","NO"}.Select(v=>Group(v,rows.Where(r=>r.Value!.Trim()==v).Select(r=>new AnswerRespondent(r.Id,r.DisplayName)))).ToArray():rows.GroupBy(r=>r.Value!.Trim(),StringComparer.Ordinal).OrderBy(g=>g.Key,StringComparer.Ordinal).Select(g=>Group(g.Key,g.Select(r=>new AnswerRespondent(r.Id,r.DisplayName)))).ToArray();
   }
   result.Add(new(q.Id,q.Text,q.Type,q.Type=="Text"&&q.PublicText,count,groups));
  }
  return result.ToArray();
 }
}
