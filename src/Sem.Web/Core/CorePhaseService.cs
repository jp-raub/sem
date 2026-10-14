using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Core;
public sealed record PhaseView(string Id,string Kind,string Name,int MaxPicks,string? ActivateAt,string? DeactivateAt,bool IsActive);
public sealed record PhasePlanView(string EventId,string Title,long RowVersion,bool ManualOverride,PhaseView[] Phases);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomPhaseEdit(string Id,string Name,int MaxPicks,DateTimeOffset? ActivateAt,DateTimeOffset? DeactivateAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PhasePlanEdit(long RowVersion,bool ManualOverride,string ActivePhaseId,CustomPhaseEdit[] CustomPhases);
public sealed partial class CoreFoundationService
{
 private static PhasePlanView Plan(SessionEvent e) => new(e.Id,e.Title,e.RowVersion,e.ManualPhaseOverride,
  e.Phases.Where(p=>p.DeletedAt is null).OrderBy(p=>p.SortOrder).ThenBy(p=>p.Id).Select(p=>new PhaseView(p.Id,p.Kind,p.Name,p.MaxPicks,p.ActivateAt,p.DeactivateAt,p.IsActive)).ToArray());
 private async Task<SessionEvent> Managed(ApplicationDbContext db,ClaimsPrincipal principal,string id,CancellationToken ct)
 {
  var actor=await Actor(db,principal,ct);
  var e=await db.SessionEvents.Include(e=>e.Admins).Include(e=>e.Phases).SingleOrDefaultAsync(e=>e.Id==id,ct);
  if(e is null||!actor.IsAdmin&&!e.Admins.Any(a=>a.UserId==actor.Id))throw new UnauthorizedAccessException();
  if(e.Phases.Count(p=>p.IsActive)!=1)throw new InvalidOperationException("Event must have exactly one active phase.");
  return e;
 }
 public async Task<PhasePlanView> ReadPhasePlanAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default)
 {await using var db=await factory.CreateDbContextAsync(ct);return Plan(await Managed(db,principal,id,ct));}
 private static EventPhase Automatic(SessionEvent e,DateTimeOffset now) => e.Phases
  .Where(p=>p.DeletedAt is null&&p.Kind=="Custom"&&p.ActivateAt is not null&&DateTimeOffset.Parse(p.ActivateAt)<=now&&(p.DeactivateAt is null||DateTimeOffset.Parse(p.DeactivateAt)>now))
  .OrderByDescending(p=>p.ActivateAt,StringComparer.Ordinal).ThenBy(p=>p.SortOrder).ThenBy(p=>p.Id).FirstOrDefault()
  ??e.Phases.Single(p=>p.Kind==(now>=DateTimeOffset.Parse(e.ScheduledDate)?"View":"Hidden"));
 private static async Task Switch(ApplicationDbContext db,SessionEvent e,EventPhase target,CancellationToken ct)
 {
  // Clear then activate inside the same transaction: SQLite checks its unique index per statement.
  foreach(var p in e.Phases.Where(p=>p.IsActive))p.IsActive=false;
  await db.SaveChangesAsync(ct);target.IsActive=true;e.PhaseKind=target.Kind=="Hidden"?"Hidden":"View";
 }
 public async Task<PhasePlanView> SavePhasePlanAsync(ClaimsPrincipal principal,string id,PhasePlanEdit edit,CancellationToken ct=default)
 {
  if(edit.CustomPhases is null||edit.CustomPhases.Any(p=>p is null)||edit.CustomPhases.Select(p=>p.Id).Distinct(StringComparer.Ordinal).Count()!=edit.CustomPhases.Length)
   throw new ValidationException("フェーズ入力が重複／不正です。");
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var e=await Managed(db,principal,id,ct);var actor=await Actor(db,principal,ct);
  if(e.RowVersion!=edit.RowVersion)throw new CoreConflictException();
  var observationNow=clock.GetUtcNow();
  var priorEffectivePhase=e.ManualPhaseOverride?e.Phases.Single(p=>p.IsActive):Automatic(e,observationNow);
  CoreEventImportHistory.Observe(e,priorEffectivePhase,observationNow);
  var drafts=new List<EventPhase>();var order=2;
  foreach(var d in edit.CustomPhases)
  {
   var name=Text(d.Name,100,"フェーズ名",true);
   if(d.MaxPicks < -1 || d.ActivateAt is not null&&d.DeactivateAt is not null&&d.DeactivateAt<=d.ActivateAt)throw new ValidationException("最大登録曲数／有効期間を確認してください。");
   var p=e.Phases.SingleOrDefault(p=>p.Id==d.Id);
   if(p is null)
   {
    if(!Guid.TryParse(d.Id,out _)||await db.Phases.AnyAsync(p=>p.Id==d.Id,ct))throw new ValidationException("新規フェーズIdが不正です。");
    p=new(){Id=d.Id,EventId=e.Id};e.Phases.Add(p);
   }
   if(p.Kind!="Custom"||p.DeletedAt is not null)throw new ValidationException("既定／削除済みフェーズは編集できません。");
   p.Name=name;p.MaxPicks=d.MaxPicks;p.ActivateAt=d.ActivateAt is null?null:Utc(d.ActivateAt.Value);p.DeactivateAt=d.DeactivateAt is null?null:Utc(d.DeactivateAt.Value);p.SortOrder=order++;drafts.Add(p);
  }
  var removed=e.Phases.Where(p=>p.Kind=="Custom"&&p.DeletedAt is null&&!drafts.Contains(p)).ToArray();
  foreach(var p in removed)p.DeletedAt=Utc(clock.GetUtcNow());
  var target=edit.ManualOverride?e.Phases.SingleOrDefault(p=>p.Id==edit.ActivePhaseId&&p.DeletedAt is null):Automatic(e,clock.GetUtcNow());
  if(target is null)throw new ValidationException("有効なフェーズは削除できません。先に別のフェーズを選択してください。");
  // Tombstones and new custom phases must not be saved before the old active row is cleared.
  foreach(var p in removed)p.DeletedAt=null;
  CoreEventImportHistory.Observe(e,target,clock.GetUtcNow());
  await Switch(db,e,target,ct);
  foreach(var p in removed)p.DeletedAt=Utc(clock.GetUtcNow());
  e.ManualPhaseOverride=edit.ManualOverride;e.RowVersion++;
  Audit(db,actor.Id,"PhasePlanSaved",e.Id);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Plan(e);
 }
 public async Task<int> EvaluateAutomaticPhasesAsync(CancellationToken ct=default)
 {
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var events=await db.SessionEvents.Include(e=>e.Phases).Where(e=>!e.ManualPhaseOverride).ToListAsync(ct);var changed=0;var now=clock.GetUtcNow();
  foreach(var e in events)
  {
   if(e.Phases.Count(p=>p.IsActive)!=1)throw new InvalidOperationException("Event must have exactly one active phase.");
   var target=Automatic(e,now);var historyChanged=CoreEventImportHistory.Observe(e,target,now);
   if(target.IsActive){if(historyChanged)e.RowVersion++;continue;}
   await Switch(db,e,target,ct);e.RowVersion++;Audit(db,"system","PhaseAutoActivated",target.Id);changed++;
  }
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return changed;
 }
}
public sealed class PhaseWorker(IServiceScopeFactory scopes,TimeProvider clock,ILogger<PhaseWorker> logger):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken stoppingToken)
 {
  using var timer=new PeriodicTimer(TimeSpan.FromSeconds(15),clock);
  do {try {using var scope=scopes.CreateScope();await scope.ServiceProvider.GetRequiredService<CoreFoundationService>().EvaluateAutomaticPhasesAsync(stoppingToken);}
   catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
   catch(Exception e){logger.LogError(e,"Automatic phase evaluation failed; retrying without bypassing storage guards.");}
  }while(await timer.WaitForNextTickAsync(stoppingToken));
 }
}
