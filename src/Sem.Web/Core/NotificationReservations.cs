using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
using System.Security.Claims;
namespace Sem.Web.Core;
public sealed class NotificationScheduleState {
 public string EventId{get;set;}="";public string Fingerprint{get;set;}="";public long EventVersion{get;set;}public long ProgressVersion{get;set;}public long Version{get;set;}public long ChangedAt{get;set;}
}
public sealed class NotificationReservation {
 public string EventId{get;set;}="";public string Id{get;set;}="";public string Kind{get;set;}="";public string?PartId{get;set;}
 public string Basis{get;set;}="";public string?PreviousPartId{get;set;}public long?ReferenceAt{get;set;}public long?DueAt{get;set;}
 public string State{get;set;}="WaitingActual";public string?CancelReason{get;set;}public long Version{get;set;}public long ChangedAt{get;set;}
}
public sealed record NotificationReservationLedger(string EventId,string Title,bool Dormant,bool IsCurrent,
 long?Version,long?EventVersion,long?ProgressVersion,string?Fingerprint,DateTimeOffset?ChangedAt,DateTimeOffset ReadAt,NotificationReservation[] Reservations);
public static class ReservationComparison {
 public static string State(NotificationPlanEntry e)=>e.State=="Obsolete"?"Cancelled":e.State;
 public static string?Reason(NotificationPlanEntry e)=>e.State=="Obsolete"?"TargetStarted":null;
 public static bool Same(NotificationReservation r,NotificationPlanEntry e)=>r.Id==e.Id&&r.Kind==e.Kind&&r.PartId==e.PartId&&r.Basis==e.Basis&&r.PreviousPartId==e.PreviousPartId&&r.ReferenceAt==e.ReferenceAt?.ToUnixTimeMilliseconds()&&r.DueAt==e.DueAt?.ToUnixTimeMilliseconds()&&r.State==State(e)&&r.CancelReason==Reason(e);
 public static bool Current(NotificationScheduleState?state,NotificationReservation[]saved,EventNotificationPlan plan)=>state is not null&&state.Fingerprint==plan.Fingerprint&&state.EventVersion==plan.RowVersion&&state.ProgressVersion==plan.ProgressVersion&&plan.Entries.All(e=>saved.Any(r=>Same(r,e)))&&saved.All(r=>plan.Entries.Any(e=>e.Id==r.Id)||r.State=="Cancelled"&&r.CancelReason=="PartRemoved");
}
// This is a dormant plan ledger. It never creates a recipient, transport claim or delivery.
public sealed class NotificationReservationReconciler(IDbContextFactory<ApplicationDbContext> factory,TimeProvider clock){
 public async Task<(string Cursor,int Scanned,int Changed,int Failed)> Batch(string cursor,Action<Type>?failed=null,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);
  var ids=await db.SessionEvents.AsNoTracking().Where(x=>x.Id.CompareTo(cursor)>0).OrderBy(x=>x.Id).Select(x=>x.Id).Take(20).ToArrayAsync(ct);
  if(ids.Length==0)return ("",0,0,0);int changed=0,errors=0;
  foreach(var id in ids){try{if(await Reconcile(id,ct))changed++;}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch(Exception ex){errors++;failed?.Invoke(ex.GetType());}}
  return (ids[^1],ids.Length,changed,errors);
 }
 public async Task<bool> Reconcile(string id,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var e=await db.SessionEvents.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(e is null)return false;
  var plan=await CoreFoundationService.BuildNotificationPlan(db,e,clock.GetUtcNow(),ct);
  var state=await db.NotificationScheduleStates.SingleOrDefaultAsync(x=>x.EventId==id,ct);var saved=await db.NotificationReservations.Where(x=>x.EventId==id).ToArrayAsync(ct);
  if(ReservationComparison.Current(state,saved,plan))return false;
  var now=clock.GetUtcNow().ToUnixTimeMilliseconds();var version=checked((state?.Version??0)+1);
  if(state is null){state=new(){EventId=id};db.NotificationScheduleStates.Add(state);}state.Version=version;state.Fingerprint=plan.Fingerprint;state.EventVersion=plan.RowVersion;state.ProgressVersion=plan.ProgressVersion;state.ChangedAt=now;
  foreach(var entry in plan.Entries){var row=saved.SingleOrDefault(x=>x.Id==entry.Id);if(row is not null&&ReservationComparison.Same(row,entry))continue;
   if(row is null){row=new(){EventId=id,Id=entry.Id};db.NotificationReservations.Add(row);}row.Kind=entry.Kind;row.PartId=entry.PartId;row.Basis=entry.Basis;row.PreviousPartId=entry.PreviousPartId;row.ReferenceAt=entry.ReferenceAt?.ToUnixTimeMilliseconds();row.DueAt=entry.DueAt?.ToUnixTimeMilliseconds();row.State=ReservationComparison.State(entry);row.CancelReason=ReservationComparison.Reason(entry);row.Version=version;row.ChangedAt=now;
  }
  foreach(var row in saved.Where(r=>!plan.Entries.Any(x=>x.Id==r.Id)&&!(r.State=="Cancelled"&&r.CancelReason=="PartRemoved"))){row.State="Cancelled";row.CancelReason="PartRemoved";row.Version=version;row.ChangedAt=now;}
  db.Audits.Add(new(){ActorId="system",Action="NotificationReservationsReconciled",EntityId=id,At=clock.GetUtcNow().ToUnixTimeSeconds()});
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;
 }
}
public sealed class NotificationReservationWorker(IServiceScopeFactory scopes,ILogger<NotificationReservationWorker> logger):BackgroundService{
 protected override async Task ExecuteAsync(CancellationToken ct){string cursor="";using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));while(await timer.WaitForNextTickAsync(ct)){
  try{
   await using var scope=scopes.CreateAsyncScope();var result=await scope.ServiceProvider.GetRequiredService<NotificationReservationReconciler>().Batch(cursor,type=>logger.LogWarning("Notification reservation reconciliation failed; type={Type}",type.Name),ct);cursor=result.Cursor;
  }catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}catch(Exception ex){logger.LogWarning("Notification reservation iteration failed; type={Type}",ex.GetType().Name);}
 }}
}
public sealed partial class CoreFoundationService {
 public async Task<NotificationReservationLedger> ReadNotificationReservationsAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);var plan=await BuildNotificationPlan(db,e,clock.GetUtcNow(),ct);
  var state=await db.NotificationScheduleStates.AsNoTracking().SingleOrDefaultAsync(x=>x.EventId==id,ct);var rows=await db.NotificationReservations.AsNoTracking().Where(x=>x.EventId==id).OrderBy(x=>x.Id).ToArrayAsync(ct);await tx.CommitAsync(ct);
  return new(id,e.Title,true,ReservationComparison.Current(state,rows,plan),state?.Version,state?.EventVersion,state?.ProgressVersion,state?.Fingerprint,state is null?null:DateTimeOffset.FromUnixTimeMilliseconds(state.ChangedAt),plan.ReadAt,rows);
 }
}
