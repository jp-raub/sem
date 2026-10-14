using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

public sealed record NotificationPlanEntry(string Id,string Kind,string? PartId,string Title,string Basis,
 string? PreviousPartId,DateTimeOffset? ReferenceAt,DateTimeOffset? DueAt,string State);
public sealed record EventNotificationPlan(string EventId,string Title,long RowVersion,long ProgressVersion,
 string TimeZone,DateTimeOffset ReadAt,string Fingerprint,bool PreviewOnly,NotificationPlanEntry[] Entries);

// Read-only scheduling decisions, not subscriptions, recipient defaults, a queue or a send action.
public static class NotificationPlanCalculator {
 public static NotificationPlanEntry[] Calculate(SessionEvent e,ProgressRow[] rows,DateTimeOffset now){
  try{
   if(rows is null||rows.Any(x=>x is null||x.Original is null||x.Latest is null||x.Points is null||x.Original.Id!=x.Latest.Id||x.Original.Kind is not ("Opening" or "Song" or "Transition")||x.Original.Kind!=x.Latest.Kind||x.Points.Select(p=>p.Point).Distinct().Count()!=x.Points.Length)||rows.Select(x=>x.Original.Id).Distinct().Count()!=rows.Length)
    throw new ValidationException("通知予定のタイムパートを確認してください。");
   DateTimeOffset? Point(ProgressRow row,string point)=>row.Points.SingleOrDefault(p=>p.Point==point)?.Actual;
   string Pending(DateTimeOffset?due,bool obsolete)=>obsolete?"Obsolete":due is null?"WaitingActual":due<=now?"Due":"Planned";
   var start=TimetableCalculator.Instant(e.ScheduledDate);var reference=e.OpensAt is null?start:TimetableCalculator.Instant(e.OpensAt);
   if(reference>start)throw new ValidationException("開場は開始以前にしてください。");
   var openingDone=rows.Any(r=>r.Original.Kind=="Opening"&&Point(r,"Start") is not null)||rows.Any(r=>r.Original.Kind!="Opening"&&(Point(r,"PrepStart") is not null||Point(r,"Start") is not null));
   var due=reference.AddMinutes(-120);var entries=new List<NotificationPlanEntry>{new("opening","OpeningReminder",null,"開催前通知",e.OpensAt is null?"EventStartMinus120Minutes":"OpeningMinus120Minutes",null,reference,due,Pending(due,openingDone))};
   for(int i=0;i<rows.Length;i++){
    var row=rows[i];if(row.Original.Kind!="Song")continue;
    if(row.Latest.PrepStart is null||row.Latest.SongId is null)throw new ValidationException("曲の準備開始基準を確認してください。");
    var previous=i>0?rows[i-1]:null;DateTimeOffset?basisAt=previous is null?row.Latest.PrepStart:Point(previous,"Start");
    DateTimeOffset?at=previous is null?basisAt!.Value.AddMinutes(-10):basisAt;
    var obsolete=Point(row,"PrepStart") is not null||Point(row,"Start") is not null||Point(row,"End") is not null;
    entries.Add(new("song:"+row.Original.Id,"SongPreparation",row.Original.Id,row.Latest.Title,previous is null?"FirstPartPreparationMinus10Minutes":"PreviousPartActualStart",previous?.Original.Id,basisAt,at,Pending(at,obsolete)));
   }
   return entries.ToArray();
  }catch(Exception ex)when(ex is FormatException or OverflowException or ArgumentOutOfRangeException or InvalidOperationException){throw new ValidationException("通知予定の日時・進行実績を確認してください。");}
 }
}
public sealed partial class CoreFoundationService {
 public async Task<EventNotificationPlan> ReadNotificationPlanAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var e=await Managed(db,principal,id,ct);
  var state=await db.EventProgressStates.AsNoTracking().SingleOrDefaultAsync(x=>x.EventId==id,ct);
  ProgressBaseline baseline;
  try{baseline=state is null?await BaselineWithPolicies(db,e,true,ct):JsonSerializer.Deserialize<ProgressBaseline>(state.PlanJson)??throw new ValidationException("進行計画を確認してください。");}
  catch(JsonException){throw new ValidationException("進行計画を確認してください。");}
  var marks=await db.ProgressMarks.AsNoTracking().Where(x=>x.EventId==id).ToArrayAsync(ct);
  var estimates=await db.ProgressEstimates.AsNoTracking().Where(x=>x.EventId==id).ToArrayAsync(ct);
  var rows=ProgressCalculator.Calculate(baseline,marks,estimates);var now=clock.GetUtcNow();var entries=NotificationPlanCalculator.Calculate(e,rows,now);
  var fingerprint=SemEndpoints.Hash(JsonSerializer.Serialize(new{e.Id,e.RowVersion,ProgressVersion=state?.RowVersion??0,Entries=entries.Select(x=>new{x.Id,x.Kind,x.PartId,x.Basis,x.PreviousPartId,x.ReferenceAt,x.DueAt,Obsolete=x.State=="Obsolete"})}));
  await tx.CommitAsync(ct);return new(e.Id,e.Title,e.RowVersion,state?.RowVersion??0,"Asia/Tokyo",now,fingerprint,true,entries);
 }
}
