using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Core;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimePartEdit(string Id,string Kind,string? SongId,string Name,int DurationSeconds,string? Icon=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimetableEdit(long RowVersion,bool RoundStarts,string ClockFormat,string DurationFormat,string SongIcon,DateTimeOffset? PlannedEndAt,TimePartEdit[] Parts,bool AcknowledgeOverrun=false);
public sealed record TimetablePlan(string EventId,string Title,long RowVersion,bool CanManage,string ScheduledDate,string? OpensAt,int DefaultPrepSeconds,bool RoundStarts,string ClockFormat,string DurationFormat,string SongIcon,string? PlannedEndAt,TimePartEdit[] Parts,Song[] Songs,CalculatedTimetable Calculation);
public sealed partial class CoreFoundationService {
 private static async Task<TimetablePlan> Timetable(ApplicationDbContext db,SessionEvent e,bool manager,CancellationToken ct){
  var songs=await db.Songs.AsNoTracking().Where(x=>x.EventId==e.Id&&x.DeletedAt==null).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).ToArrayAsync(ct);
  var parts=await db.TimeParts.AsNoTracking().Where(x=>x.EventId==e.Id&&x.DeletedAt==null).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).ToArrayAsync(ct);
  return new(e.Id,e.Title,e.RowVersion,manager,e.ScheduledDate,e.OpensAt,e.DefaultPrepSeconds,e.RoundStarts,e.ClockFormat,e.DurationFormat,e.SongIcon,e.PlannedEndAt,parts.Select(x=>new TimePartEdit(x.Id,x.Kind,x.SongId,x.Name,x.DurationSeconds,x.Icon)).ToArray(),songs,TimetableCalculator.Calculate(e,parts,songs));
 }
 public async Task<TimetablePlan> ReadTimetableAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default){await using var db=await factory.CreateDbContextAsync(ct);var(e,_,_,manager)=await ParticipantEvent(db,principal,id,ct);return await Timetable(db,e,manager,ct);}
 public async Task<TimetablePlan> SaveTimetableAsync(ClaimsPrincipal principal,string id,TimetableEdit edit,CancellationToken ct=default){
  if(edit.RowVersion<0||edit.ClockFormat is not ("seconds" or "minutes")||edit.DurationFormat is not ("units" or "colon")||!TimetableCalculator.Icons.Contains(edit.SongIcon)||edit.Parts is null||edit.Parts.Length>600||edit.Parts.Any(x=>x is null)||edit.Parts.Select(x=>x.Id).Distinct().Count()!=edit.Parts.Length)throw new ValidationException("識別子・行数・表示設定を確認してください。");
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);var actor=await Actor(db,principal,ct);if(e.RowVersion!=edit.RowVersion)throw new CoreConflictException();
  if(edit.PlannedEndAt is DateTimeOffset end&&end<TimetableCalculator.Instant(e.ScheduledDate))throw new ValidationException("終了予定は開始以降にしてください。");
  var old=await db.TimeParts.Where(x=>x.EventId==id).ToListAsync(ct);var songs=await db.Songs.Where(x=>x.EventId==id&&x.DeletedAt==null).ToArrayAsync(ct);var drafts=new List<TimePart>();
  foreach(var d in edit.Parts){var prior=old.SingleOrDefault(x=>x.Id==d.Id);if(prior is not null&&(prior.DeletedAt!=null||prior.Kind!=d.Kind||prior.SongId!=d.SongId)||prior is null&&(!Guid.TryParse(d.Id,out _)||await db.TimeParts.AnyAsync(x=>x.Id==d.Id,ct)))throw new ValidationException("識別子・種類・曲参照は変更できません。削除済み行も復活できません。");
   if(d.Kind=="Song"){if(prior is null||!songs.Any(x=>x.Id==d.SongId)||d.Name!=""||d.DurationSeconds!=0||d.Icon is not null&&!TimetableCalculator.Icons.Contains(d.Icon))throw new ValidationException("既存の曲タイムパートを指定してください。曲内容は曲・枠管理で編集します。");}
   else if(d.Kind=="Transition"){if(d.SongId is not null||d.Icon is not null||d.DurationSeconds<0)throw new ValidationException("進行は0秒以上、曲参照・曲アイコンなしで指定してください。");}
   else throw new ValidationException("開場行や不明な種類は直接編集できません。");
   drafts.Add(new(){Id=d.Id,EventId=id,Kind=d.Kind,SongId=d.SongId,Name=d.Kind=="Transition"?Text(d.Name,200,"進行名",true):"",DurationSeconds=d.DurationSeconds,Icon=d.Icon,SortOrder=drafts.Count});
  }
  if(drafts.Count(x=>x.Kind=="Song")!=songs.Length||songs.Any(s=>drafts.Count(x=>x.SongId==s.Id)!=1)||drafts.Count(x=>x.Kind=="Transition")>300)throw new ValidationException("有効な曲を各1行含めてください。曲削除は曲・枠管理から行います。");
  e.RoundStarts=edit.RoundStarts;e.ClockFormat=edit.ClockFormat;e.DurationFormat=edit.DurationFormat;e.SongIcon=edit.SongIcon;e.PlannedEndAt=edit.PlannedEndAt is null?null:Utc(edit.PlannedEndAt.Value);
  var calculated=TimetableCalculator.Calculate(e,drafts,songs);if(calculated.OverrunSeconds>0&&!edit.AcknowledgeOverrun)throw new ValidationException("終了予定を超過します。最新の超過秒数を確認してから保存してください。");
  foreach(var p in old.Where(x=>x.DeletedAt==null&&!drafts.Any(d=>d.Id==x.Id)))p.DeletedAt=Utc(clock.GetUtcNow());
  var songOrder=0;foreach(var d in drafts){var p=old.SingleOrDefault(x=>x.Id==d.Id);if(p is null){p=d;db.TimeParts.Add(p);}else{p.Name=d.Name;p.DurationSeconds=d.DurationSeconds;p.Icon=d.Icon;p.SortOrder=d.SortOrder;}if(d.SongId is string sid)songs.Single(s=>s.Id==sid).SortOrder=songOrder++;}
  e.RowVersion++;Audit(db,actor.Id,"TimetableSaved",id);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await ReadTimetableAsync(principal,id,ct);
 }
 // The existing song editor owns song contents, never transition order or opening.
 private async Task SyncSongTimeParts(ApplicationDbContext db,SessionEvent e,SongEdit[] drafts,CancellationToken ct){
  var parts=await db.TimeParts.Where(x=>x.EventId==e.Id&&x.DeletedAt==null).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).ToListAsync(ct);
  var retained=parts.Where(x=>x.Kind=="Song"&&drafts.Any(d=>d.Id==x.SongId)).Select(x=>x.SongId).ToArray();
  if(parts.Any(x=>x.Kind=="Transition")&&!retained.SequenceEqual(drafts.Where(d=>retained.Contains(d.Id)).Select(d=>d.Id)))throw new ValidationException("進行があるイベントの並び替えはタイムテーブル画面で行ってください。");
  foreach(var p in parts.Where(x=>x.Kind=="Song"&&!drafts.Any(d=>d.Id==x.SongId)))p.DeletedAt=Utc(clock.GetUtcNow());
  var active=parts.Where(x=>x.DeletedAt==null).ToList();foreach(var d in drafts.Where(d=>!active.Any(x=>x.SongId==d.Id))){var p=new TimePart{Id=d.Id,EventId=e.Id,Kind="Song",SongId=d.Id};active.Add(p);db.TimeParts.Add(p);}
  if(!active.Any(x=>x.Kind=="Transition"))active=drafts.Select(d=>active.Single(x=>x.SongId==d.Id)).ToList();for(var i=0;i<active.Count;i++)active[i].SortOrder=i;
 }
}
