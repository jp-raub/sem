using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
// A personal opt-in, not a device permission, recipient snapshot or delivery queue.
public sealed class PartStartPreference {
 public string EventId {get;set;}=""; public string UserId {get;set;}=""; public string PartId {get;set;}="";
 public bool Enabled {get;set;} public long Version {get;set;} public long ChangedAt {get;set;} public long?ArmedAt{get;set;} public long?ArmedProgressVersion{get;set;}
}
public sealed record PartStartOption(string PartId,string Kind,string Title,bool Enabled,long Version);
public sealed record PartStartPreferences(string EventId,string Title,long EventVersion,bool DeliveryConnected,PartStartOption[] Parts,bool PushConfigured=false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PartStartPreferenceEdit([property:JsonRequired]string PartId,[property:JsonRequired]bool Enabled,[property:JsonRequired]long Version,[property:JsonRequired]long EventVersion);
public sealed partial class CoreFoundationService {
 public async Task<PartStartPreferences> ReadPartStartPreferencesAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var(e,actor,_,manager)=await ParticipantEvent(db,principal,id,ct);var table=await Timetable(db,e,manager,ct);
  var saved=await db.Set<PartStartPreference>().AsNoTracking().Where(x=>x.EventId==id&&x.UserId==actor.Id).ToArrayAsync(ct);
  var result=new PartStartPreferences(id,e.Title,e.RowVersion,true,table.Calculation.Rows.Select(p=>{var s=saved.SingleOrDefault(x=>x.PartId==p.Id);return new PartStartOption(p.Id,p.Kind,p.Title,s?.Enabled??false,s?.Version??0);}).ToArray(),!string.IsNullOrWhiteSpace(notificationConfiguration?["Push:PublicKey"])&&!string.IsNullOrWhiteSpace(notificationConfiguration?["Push:PrivateKey"]));
  await tx.CommitAsync(ct);return result;
 }
 public async Task SavePartStartPreferenceAsync(ClaimsPrincipal principal,string id,PartStartPreferenceEdit edit,CancellationToken ct=default){
  if(edit.Version<0||edit.EventVersion<0||string.IsNullOrWhiteSpace(edit.PartId)||edit.PartId.Length>100)throw new ValidationException("通知設定の対象と版を確認してください。");
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var(e,actor,_,manager)=await ParticipantEvent(db,principal,id,ct);if(e.RowVersion!=edit.EventVersion)throw new CoreConflictException();
  var table=await Timetable(db,e,manager,ct);if(!table.Calculation.Rows.Any(p=>p.Id==edit.PartId))throw new ValidationException("現在のタイムパートを選択してください。");
  var row=await db.Set<PartStartPreference>().SingleOrDefaultAsync(x=>x.EventId==id&&x.UserId==actor.Id&&x.PartId==edit.PartId,ct);
  if((row?.Version??0)!=edit.Version)throw new CoreConflictException();
  if((row?.Enabled??false)==edit.Enabled){await tx.CommitAsync(ct);return;}
  if(row is null){row=new(){EventId=id,UserId=actor.Id,PartId=edit.PartId};db.Add(row);}
  row.Enabled=edit.Enabled;row.Version++;row.ChangedAt=clock.GetUtcNow().ToUnixTimeMilliseconds();row.ArmedAt=edit.Enabled?row.ChangedAt:null;row.ArmedProgressVersion=edit.Enabled?(await db.EventProgressStates.Where(x=>x.EventId==id).Select(x=>(long?)x.RowVersion).SingleOrDefaultAsync(ct)??0):null;
  Audit(db,actor.Id,"PartStartPreferenceChanged",id+":"+edit.PartId+":"+(edit.Enabled?"On":"Off"));
  try{await db.SaveChangesAsync(ct);}catch(DbUpdateException ex)when(ex.InnerException is Microsoft.Data.Sqlite.SqliteException sql&&(sql.SqliteErrorCode is 5 or 6||sql.SqliteExtendedErrorCode is 1555 or 2067)){throw new CoreConflictException();}
  await tx.CommitAsync(ct);
 }
}
