using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProgressRecordEdit(string RequestId,long RowVersion,long ProgressVersion,string PartId,string Point);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProgressEstimateEdit(string RequestId,long RowVersion,long ProgressVersion,string PartId,int? PrepSeconds,int? DurationSeconds);
public sealed record ProgressMarkView(string PartId,string Point,string At);
public sealed record ProgressResult(string RequestId,long ProgressVersion,ProgressMarkView[] Marks,bool Replayed=false);
public sealed record ProgressPlan(string EventId,string Title,long RowVersion,long ProgressVersion,bool CanManage,bool EventDay,bool CanRecord,ProgressNext? NextAction,ProgressSummary Summary,ProgressRow[] ManagerRows,TimedPart[] OwnSongs,string ClockFormat,bool RoundStarts);
public sealed partial class CoreFoundationService {
 private static async Task EnsurePlanEditable(ApplicationDbContext db,string id,CancellationToken ct){if(await db.EventProgressStates.AnyAsync(x=>x.EventId==id,ct))throw new ValidationException("当日運営を開始した計画は保護されています。時間の変更は当日見積から行ってください。");}
 private static ProgressBaseline Baseline(TimetablePlan p)=>new(p.ScheduledDate,p.RoundStarts,p.DefaultPrepSeconds,p.ClockFormat,p.PlannedEndAt,p.Calculation.Rows,p.Songs.ToDictionary(s=>s.Id,s=>s.PrepSeconds));
 public async Task<ProgressPlan> ReadProgressAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var(e,actor,_,manager)=await ParticipantEvent(db,principal,id,ct);var s=await db.EventProgressStates.SingleOrDefaultAsync(x=>x.EventId==id,ct);var b=s is null?Baseline(await Timetable(db,e,manager,ct)):JsonSerializer.Deserialize<ProgressBaseline>(s.PlanJson)!;var marks=await db.ProgressMarks.Where(x=>x.EventId==id).ToArrayAsync(ct);var edits=await db.ProgressEstimates.Where(x=>x.EventId==id).ToArrayAsync(ct);var rows=ProgressCalculator.Calculate(b,marks,edits);var next=ProgressCalculator.Next(rows);var now=clock.GetUtcNow();var day=ProgressCalculator.IsEventDay(b.ScheduledDate,now);var own=await db.SongParts.Where(x=>x.EventId==id&&x.AssignedUserId==actor.Id).Select(x=>x.SongId).Distinct().ToArrayAsync(ct);return new(id,e.Title,e.RowVersion,s?.RowVersion??0,manager,day,manager&&day&&next is not null,manager?next:null,ProgressCalculator.Summary(rows,now),manager?rows:[],rows.Where(r=>r.State!="完了"&&r.Latest.SongId is string sid&&own.Contains(sid)).Select(r=>r.Latest).ToArray(),b.ClockFormat,b.RoundStarts);
 }
 public Task<ProgressResult> RecordProgressAsync(ClaimsPrincipal p,string id,ProgressRecordEdit edit,CancellationToken ct=default)=>WriteProgress(p,id,edit.RequestId,edit.RowVersion,edit.ProgressVersion,edit.PartId,"Record",JsonSerializer.Serialize(edit),edit.Point,null,null,ct);
 public Task<ProgressResult> SaveProgressEstimateAsync(ClaimsPrincipal p,string id,ProgressEstimateEdit edit,CancellationToken ct=default)=>WriteProgress(p,id,edit.RequestId,edit.RowVersion,edit.ProgressVersion,edit.PartId,"Estimate",JsonSerializer.Serialize(edit),null,edit.PrepSeconds,edit.DurationSeconds,ct);
 private async Task<ProgressResult> WriteProgress(ClaimsPrincipal principal,string id,string requestId,long version,long progressVersion,string partId,string kind,string payload,string? point,int? prep,int? length,CancellationToken ct){
  if(!Guid.TryParse(requestId,out _)||version<0||progressVersion<0||prep<0||length<0||kind=="Record"&&point is not ("PrepStart" or "Start" or "End"))throw new ValidationException("操作・識別子・版・秒数を確認してください。");
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);var actor=await Actor(db,principal,ct);var fingerprint=SemEndpoints.Hash(kind+payload);var previous=await db.ProgressOperations.SingleOrDefaultAsync(x=>x.Id==requestId,ct);
  if(previous is not null){if(previous.EventId!=id||previous.ActorId!=actor.Id||previous.Fingerprint!=fingerprint)throw new ValidationException("操作Idは別の操作に再利用できません。");return JsonSerializer.Deserialize<ProgressResult>(previous.ResultJson)! with{Replayed=true};}
  var state=await db.EventProgressStates.SingleOrDefaultAsync(x=>x.EventId==id,ct);if(e.RowVersion!=version||(state?.RowVersion??0)!=progressVersion)throw new CoreConflictException();var baseline=state is null?Baseline(await Timetable(db,e,true,ct)):JsonSerializer.Deserialize<ProgressBaseline>(state.PlanJson)!;var now=clock.GetUtcNow();if(!ProgressCalculator.IsEventDay(baseline.ScheduledDate,now))throw new ValidationException("運営の書込みは日本時間のイベント当日だけ可能です。");
  var marks=await db.ProgressMarks.Where(x=>x.EventId==id).ToListAsync(ct);var estimates=await db.ProgressEstimates.Where(x=>x.EventId==id).ToListAsync(ct);var rows=ProgressCalculator.Calculate(baseline,marks,estimates);var row=rows.SingleOrDefault(r=>r.Original.Id==partId);if(row is null)throw new ValidationException("このイベントのタイムパートを選択してください。");
  var added=new List<ProgressMark>();var at=Utc(now);
  if(kind=="Record"){
   if(marks.Any(m=>TimetableCalculator.Instant(m.At)>now))throw new ValidationException("直前の実績より早い時刻は記録できません。");
   var lastRecord=await db.ProgressOperations.Where(x=>x.EventId==id&&x.Kind=="Record").OrderByDescending(x=>x.At).Select(x=>x.At).FirstOrDefaultAsync(ct);if(lastRecord is not null&&(now-TimetableCalculator.Instant(lastRecord)).TotalMilliseconds<650)throw new CoreConflictException();
   var next=ProgressCalculator.Next(rows);if(next is null||next.PartId!=partId||next.Point!=point)throw new CoreConflictException();
   void Add(string pid,string pt){if(marks.Any(m=>m.PartId==pid&&m.Point==pt))throw new CoreConflictException();added.Add(new(){EventId=id,PartId=pid,Point=pt,At=at,ActorId=actor.Id,OperationId=requestId});}
   Add(partId,point!);if(point=="End"){var index=Array.FindIndex(rows,r=>r.Original.Id==partId);if(index+1<rows.Length){var nr=rows[index+1].Original;Add(nr.Id,nr.Kind=="Song"?"PrepStart":"Start");}}
  }else{
   if(row.State=="完了"||row.Original.Kind=="Opening")throw new ValidationException("終了済みパートと開場行は当日見積を変更できません。");
   if(row.Original.Kind=="Song"&&(length is not null||row.Points.Any(p=>p.Point=="Start"&&p.Actual is not null))||row.Original.Kind=="Transition"&&(prep is not null||length is null))throw new ValidationException("演奏時間・開始済み準備は変更不可です。進行は所要時間のみ指定してください。");
   var estimate=estimates.SingleOrDefault(x=>x.PartId==partId);if(estimate is null){estimate=new(){EventId=id,PartId=partId};estimates.Add(estimate);db.ProgressEstimates.Add(estimate);}estimate.PrepSeconds=prep;estimate.DurationSeconds=length;ProgressCalculator.Calculate(baseline,marks,estimates);
  }
  if(state is null){state=new(){EventId=id,SourceVersion=e.RowVersion,PlanJson=JsonSerializer.Serialize(baseline),CreatedAt=at};db.EventProgressStates.Add(state);}state.RowVersion++;
  // Validate proposed chain before committing all adjacent markers and the operation together.
  ProgressCalculator.Calculate(baseline,marks.Concat(added),estimates);db.ProgressMarks.AddRange(added);var result=new ProgressResult(requestId,state.RowVersion,added.Select(x=>new ProgressMarkView(x.PartId,x.Point,x.At)).ToArray());db.ProgressOperations.Add(new(){Id=requestId,EventId=id,ActorId=actor.Id,Kind=kind,At=at,Fingerprint=fingerprint,ResultJson=JsonSerializer.Serialize(result)});Audit(db,actor.Id,kind=="Record"?"ProgressRecorded":"ProgressEstimateSaved",requestId);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return result;
 }
}
