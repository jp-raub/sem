using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Core;
public sealed record PartDefinitionView(string Id,string Name,string Category,string? SourcePresetId);
public sealed record PartDefinitionPlan(string? EventId,string Title,long RowVersion,PartDefinitionView[] Definitions);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PartDefinitionEdit(string Id,string Name,string Category);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PartDefinitionPlanEdit(long RowVersion,PartDefinitionEdit[] Definitions);
public sealed partial class CoreFoundationService {
 private static PartDefinitionEdit[] ValidParts(PartDefinitionPlanEdit edit) {
  if(edit.RowVersion<0||edit.Definitions is null||edit.Definitions.Length>100||edit.Definitions.Any(x=>x is null)||edit.Definitions.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=edit.Definitions.Length)
   throw new ValidationException("定義の件数／識別子／版を確認してください。");
  var rows=edit.Definitions.Select(d=> {
   var name=Text(d.Name,40,"パート名",true);
   if(name.Any(c=>"<>&\"'|".Contains(c)||char.IsControl(c))||d.Category is not ("Sing" or "Play"))throw new ValidationException("パート名は安全な1〜40文字、分類はSing／Playで指定してください。");
   return d with{Name=name};
  }).ToArray();
  if(rows.Select(x=>x.Name.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count()!=rows.Length)throw new ValidationException("同じ名前のパートを重複登録できません。");
  return rows;
 }
 public async Task<PartDefinitionPlan> ReadDefinitionsAsync(ClaimsPrincipal principal,string? eventId,CancellationToken ct=default) {
  await using var db=await factory.CreateDbContextAsync(ct);
  if(eventId is null){var a=await Actor(db,principal,ct);if(!a.IsAdmin)throw new UnauthorizedAccessException();
   var state=await db.PartPresetStates.SingleAsync(ct);return new(null,"新規イベント用プリセット",state.RowVersion,await db.PartPresets.Where(x=>x.DeletedAt==null).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).Select(x=>new PartDefinitionView(x.Id,x.Name,x.Category,null)).ToArrayAsync(ct));}
  var e=await Managed(db,principal,eventId,ct);
  return new(e.Id,e.Title,e.RowVersion,await db.EventPartDefinitions.Where(x=>x.EventId==e.Id&&x.DeletedAt==null).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).Select(x=>new PartDefinitionView(x.Id,x.Name,x.Category,x.SourcePresetId)).ToArrayAsync(ct));
 }
 public async Task<PartDefinitionPlan> SaveDefinitionsAsync(ClaimsPrincipal principal,string? eventId,PartDefinitionPlanEdit edit,CancellationToken ct=default) {
  var drafts=ValidParts(edit);await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var actor=await Actor(db,principal,ct);
  if(eventId is null){
   if(!actor.IsAdmin)throw new UnauthorizedAccessException();var state=await db.PartPresetStates.SingleAsync(ct);if(state.RowVersion!=edit.RowVersion)throw new CoreConflictException();
   var rows=await db.PartPresets.ToListAsync(ct);
   foreach(var d in drafts){var old=rows.SingleOrDefault(x=>x.Id==d.Id);if(old is not null&&old.DeletedAt is not null||old is null&&(!Guid.TryParse(d.Id,out _)||await db.EventPartDefinitions.AnyAsync(x=>x.Id==d.Id,ct)))throw new ValidationException("定義の識別子を確認してください。");}
   foreach(var old in rows.Where(x=>x.DeletedAt is null)){old.NormalizedName="|"+old.Id;if(!drafts.Any(d=>d.Id==old.Id))old.DeletedAt=Utc(clock.GetUtcNow());}
   await db.SaveChangesAsync(ct);var order=0;
   foreach(var d in drafts){var row=rows.SingleOrDefault(x=>x.Id==d.Id);if(row is null){row=new(){Id=d.Id};db.PartPresets.Add(row);}row.Name=d.Name;row.NormalizedName=d.Name.ToUpperInvariant();row.Category=d.Category;row.SortOrder=order++;}
   state.RowVersion++;Audit(db,actor.Id,"PartPresetsSaved","1");
  } else {
   var e=await Managed(db,principal,eventId,ct);if(e.RowVersion!=edit.RowVersion)throw new CoreConflictException();
   var rows=await db.EventPartDefinitions.Where(x=>x.EventId==e.Id).ToListAsync(ct);
   foreach(var d in drafts){var old=rows.SingleOrDefault(x=>x.Id==d.Id);if(old is not null&&old.DeletedAt is not null||old is null&&(!Guid.TryParse(d.Id,out _)||await db.EventPartDefinitions.AnyAsync(x=>x.Id==d.Id,ct)||await db.PartPresets.AnyAsync(x=>x.Id==d.Id,ct)))throw new ValidationException("定義の識別子を確認してください。");}
   var protectedIds=rows.Where(x=>x.DeletedAt is null&&(!drafts.Any(d=>d.Id==x.Id)||drafts.Any(d=>d.Id==x.Id&&d.Category!=x.Category))).Select(x=>x.Id).ToArray();
   if(await db.SongParts.AnyAsync(x=>protectedIds.Contains(x.PartDefinitionId),ct))throw new ValidationException("曲の枠で使用中の定義は削除／分類変更できません。名前は同じ識別子のまま変更できます。");
   foreach(var old in rows.Where(x=>x.DeletedAt is null)){old.NormalizedName="|"+old.Id;if(!drafts.Any(d=>d.Id==old.Id))old.DeletedAt=Utc(clock.GetUtcNow());}
   await db.SaveChangesAsync(ct);var order=0;
   foreach(var d in drafts){var row=rows.SingleOrDefault(x=>x.Id==d.Id);if(row is null){row=new(){Id=d.Id,EventId=e.Id};db.EventPartDefinitions.Add(row);}row.Name=d.Name;row.NormalizedName=d.Name.ToUpperInvariant();row.Category=d.Category;row.SortOrder=order++;}
   e.RowVersion++;Audit(db,actor.Id,"EventPartDefinitionsSaved",e.Id);
  }
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await ReadDefinitionsAsync(principal,eventId,ct);
 }
 private static async Task CopyPartPresetsAsync(ApplicationDbContext db,string eventId,CancellationToken ct) {
  var presets=await db.PartPresets.Where(x=>x.DeletedAt==null).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).ToListAsync(ct);
  foreach(var p in presets)db.EventPartDefinitions.Add(new(){EventId=eventId,SourcePresetId=p.Id,Name=p.Name,NormalizedName=p.NormalizedName,Category=p.Category,SortOrder=p.SortOrder});
 }
}
