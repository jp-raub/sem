using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
public sealed record SongPartView(string Id,string DefinitionId,string Name,string Category,bool Assigned);
public sealed record SongView(string Id,string Title,string Artist,int PerformanceSeconds,int? PrepSeconds,SongPartView[] Parts);
public sealed record SongPlan(string EventId,string Title,long RowVersion,int DefaultPrepSeconds,PartDefinitionView[] Definitions,SongView[] Songs);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SongEdit(string Id,string Title,string Artist,int PerformanceSeconds,int? PrepSeconds,string[] DefinitionIds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SongPlanEdit(long RowVersion,int DefaultPrepSeconds,SongEdit[] Songs);
public sealed partial class CoreFoundationService {
 public async Task<SongPlan> ReadSongsAsync(ClaimsPrincipal principal,string eventId,CancellationToken ct=default) {
  await using var db=await factory.CreateDbContextAsync(ct);var e=await Managed(db,principal,eventId,ct);
  var definitions=await db.EventPartDefinitions.Where(x=>x.EventId==e.Id&&x.DeletedAt==null).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).Select(x=>new PartDefinitionView(x.Id,x.Name,x.Category,x.SourcePresetId)).ToArrayAsync(ct);
  var parts=await db.SongParts.Where(x=>x.EventId==e.Id).ToArrayAsync(ct);var rows=await db.Songs.Where(x=>x.EventId==e.Id&&x.DeletedAt==null).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).ToArrayAsync(ct);
  return new(e.Id,e.Title,e.RowVersion,e.DefaultPrepSeconds,definitions,rows.Select(x=>new SongView(x.Id,x.Title,x.Artist,x.PerformanceSeconds,x.PrepSeconds,definitions.SelectMany(d=>parts.Where(p=>p.SongId==x.Id&&p.PartDefinitionId==d.Id).Select(p=>new SongPartView(p.Id,d.Id,d.Name,d.Category,p.AssignedUserId!=null))).ToArray())).ToArray());
 }
 public async Task<SongPlan> SaveSongsAsync(ClaimsPrincipal principal,string eventId,SongPlanEdit edit,CancellationToken ct=default) {
  if(edit.RowVersion<0||edit.DefaultPrepSeconds<0||edit.Songs is null||edit.Songs.Length>300||edit.Songs.Any(x=>x is null)||edit.Songs.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=edit.Songs.Length)throw new ValidationException("曲数／識別子／版／準備秒数を確認してください。");
  var drafts=edit.Songs.Select(x=>{if(x.PerformanceSeconds<1||x.PrepSeconds<0||x.DefinitionIds is null||x.DefinitionIds.Length is <1 or >100||x.DefinitionIds.Distinct(StringComparer.Ordinal).Count()!=x.DefinitionIds.Length)throw new ValidationException("演奏は1秒以上、準備は0秒以上または継承、枠は各定義1つ以上で指定してください。");return x with{Title=Text(x.Title,200,"曲名",true),Artist=Text(x.Artist,200,"アーティスト")};}).ToArray();
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,eventId,ct);var actor=await Actor(db,principal,ct);if(e.RowVersion!=edit.RowVersion)throw new CoreConflictException();
  var rows=await db.Songs.Where(x=>x.EventId==e.Id).ToListAsync(ct);var parts=await db.SongParts.Where(x=>x.EventId==e.Id).ToListAsync(ct);var defs=await db.EventPartDefinitions.Where(x=>x.EventId==e.Id&&x.DeletedAt==null).Select(x=>x.Id).ToArrayAsync(ct);
  foreach(var d in drafts){var old=rows.SingleOrDefault(x=>x.Id==d.Id);if(old is not null&&old.DeletedAt!=null||old is null&&(!Guid.TryParse(d.Id,out _)||await db.Songs.AnyAsync(x=>x.Id==d.Id,ct)))throw new ValidationException("曲の識別子を確認してください。削除済みの曲は復活できません。");if(d.DefinitionIds.Any(x=>!defs.Contains(x)))throw new ValidationException("このイベントの有効なパート定義を選択してください。");
   if(parts.Where(x=>x.SongId==d.Id&&x.AssignedUserId!=null).GroupBy(x=>x.PartDefinitionId).Any(g=>g.Count()>1))throw new ValidationException("同種の登録済み複数枠は、重複枠を解除してから変更してください。");
  }
  var removed=parts.Where(p=>!drafts.Any(d=>d.Id==p.SongId&&d.DefinitionIds.Contains(p.PartDefinitionId))).ToArray();if(removed.Any(p=>p.AssignedUserId!=null))throw new ValidationException("担当者のいる曲／枠は削除できません。参加解除を先に行ってください。");
  db.SongParts.RemoveRange(removed);
  foreach(var old in rows.Where(x=>x.DeletedAt==null&&!drafts.Any(d=>d.Id==x.Id)))old.DeletedAt=Utc(clock.GetUtcNow());
  var order=0;foreach(var d in drafts){var row=rows.SingleOrDefault(x=>x.Id==d.Id);if(row is null){row=new(){Id=d.Id,EventId=e.Id};db.Songs.Add(row);}row.Title=d.Title;row.Artist=d.Artist;row.PerformanceSeconds=d.PerformanceSeconds;row.PrepSeconds=d.PrepSeconds;row.SortOrder=order++;
   foreach(var id in d.DefinitionIds){var existing=parts.Where(p=>p.SongId==d.Id&&p.PartDefinitionId==id).ToArray();if(existing.Length==0)db.SongParts.Add(new(){EventId=e.Id,SongId=d.Id,PartDefinitionId=id});else if(existing.Length>1){var keep=existing.FirstOrDefault(p=>p.AssignedUserId!=null)??existing[0];db.SongParts.RemoveRange(existing.Where(p=>p.Id!=keep.Id));}}
  }
  e.DefaultPrepSeconds=edit.DefaultPrepSeconds;e.RowVersion++;Audit(db,actor.Id,"SongPlanSaved",e.Id);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await ReadSongsAsync(principal,eventId,ct);
 }
}
