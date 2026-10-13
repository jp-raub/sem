using System.ComponentModel.DataAnnotations;
namespace Sem.Web.Core;
public sealed record ProgressBaseline(string ScheduledDate,bool RoundStarts,int DefaultPrepSeconds,string ClockFormat,string? PlannedEndAt,TimedPart[] Rows,Dictionary<string,int?> PrepInputs,ShorteningPolicy[]? PlannedPolicies=null);
public sealed record ProgressNext(string PartId,string Point,string Label);
public sealed record ProgressPoint(string Point,DateTimeOffset Original,DateTimeOffset Latest,DateTimeOffset? Actual);
public sealed record ProgressRow(TimedPart Original,TimedPart Latest,ProgressPoint[] Points,string State,int? PrepInput);
public sealed record ProgressSummary(string State,string? CurrentTitle,string? NextLabel,DateTimeOffset? Target,TimedPart? NextPart,long DeltaMilliseconds,DateTimeOffset Now);
public static class ProgressCalculator {
 private static DateTimeOffset? Actual(IEnumerable<ProgressMark> marks,string id,string point){var m=marks.SingleOrDefault(x=>x.PartId==id&&x.Point==point);return m is null?null:TimetableCalculator.Instant(m.At);}
 private static DateTimeOffset Ceil(DateTimeOffset v){var t=v.UtcTicks;var r=t%TimeSpan.TicksPerMinute;return new(r==0?t:checked(t+TimeSpan.TicksPerMinute-r),TimeSpan.Zero);}
 public static ProgressRow[] Calculate(ProgressBaseline b,IEnumerable<ProgressMark> stored,IEnumerable<ProgressEstimate> estimates){
  try {var marks=stored.ToArray();var edits=estimates.ToArray();var rows=new List<ProgressRow>();DateTimeOffset? cursor=null;
   foreach(var r in b.Rows){var e=edits.SingleOrDefault(x=>x.PartId==r.Id);var prep=r.Kind=="Song"?(e is null?r.ConfiguredPrepSeconds!.Value:e.PrepSeconds??b.DefaultPrepSeconds):0;var length=r.Kind=="Transition"?(e?.DurationSeconds??r.LengthSeconds):r.LengthSeconds;
    var begin=r.Kind=="Opening"?r.Start:cursor??r.PrepStart??r.Start;DateTimeOffset? p=null;if(r.Kind=="Song")p=Actual(marks,r.Id,"PrepStart")??(b.RoundStarts?Ceil(begin):begin);
    var predicted=r.Kind=="Song"?p!.Value.AddSeconds(prep):begin;var start=Actual(marks,r.Id,"Start")??(r.Kind=="Song"&&b.RoundStarts?Ceil(predicted):predicted);var end=Actual(marks,r.Id,"End")??start.AddSeconds(length);cursor=end;
    var latest=r with{PrepStart=p,Start=start,End=end,LengthSeconds=length,ConfiguredPrepSeconds=r.Kind=="Song"?prep:null};var points=new List<ProgressPoint>();if(p is not null)points.Add(new("PrepStart",r.PrepStart!.Value,p.Value,Actual(marks,r.Id,"PrepStart")));points.Add(new("Start",r.Start,start,Actual(marks,r.Id,"Start")));points.Add(new("End",r.End,end,Actual(marks,r.Id,"End")));
    var state=Actual(marks,r.Id,"End") is not null?"完了":Actual(marks,r.Id,"Start") is not null?r.Kind=="Song"?"演奏中":r.Kind=="Opening"?"受付中":"進行中":Actual(marks,r.Id,"PrepStart") is not null?"準備中":"開始待ち";rows.Add(new(r,latest,points.ToArray(),state,r.Kind=="Song"?(e is null?b.PrepInputs.GetValueOrDefault(r.SongId!):e.PrepSeconds):null));
   }return rows.ToArray();
  }catch(Exception e)when(e is OverflowException or ArgumentOutOfRangeException){throw new ValidationException("当日見積が日時の計算範囲を超えています。");}
 }
 public static ProgressNext? Next(ProgressRow[] rows){var i=Array.FindIndex(rows,r=>r.State!="完了");if(i<0)return null;var r=rows[i];var point=r.Points.First(x=>x.Actual is null).Point;var label=point=="PrepStart"?"準備開始":point=="Start"?r.Original.Kind=="Song"?"演奏開始":"開始":i+1<rows.Length?"終了 ＋ "+rows[i+1].Original.Title+(rows[i+1].Original.Kind=="Song"?"の準備開始":"の開始"):"終了（イベント完了）";return new(r.Original.Id,point,label);}
 public static ProgressSummary Summary(ProgressRow[] rows,DateTimeOffset now){var i=Array.FindIndex(rows,r=>r.State!="完了");if(i<0){var last=rows.LastOrDefault();return new(rows.Length==0?"パート未設定":"終了",null,null,null,null,last is null?0:(long)(last.Latest.End-last.Original.End).TotalMilliseconds,now);}var r=rows[i];var action=Next(rows)!;var target=r.Points.Single(p=>p.Point==action.Point).Latest;var prep=r.Points.SingleOrDefault(p=>p.Point=="PrepStart");var start=r.Points.Single(p=>p.Point=="Start");double delta;
  if(start.Actual is DateTimeOffset at)delta=Math.Max((at-start.Original).TotalMilliseconds,(now-r.Original.End).TotalMilliseconds);else if(prep?.Actual is DateTimeOffset pat)delta=Math.Max((pat-prep.Original).TotalMilliseconds,(now-start.Original).TotalMilliseconds);else delta=Math.Max(0,(now-(prep?.Original??start.Original)).TotalMilliseconds);
  return new(r.State,r.Original.Title,action.Label,target,i+1<rows.Length?rows[i+1].Latest:null,(long)delta,now);
 }
 public static bool IsEventDay(string date,DateTimeOffset now)=>TimetableCalculator.Instant(date).ToOffset(TimeSpan.FromHours(9)).Date==now.ToOffset(TimeSpan.FromHours(9)).Date;
 public static string Remaining(DateTimeOffset target,DateTimeOffset now){var ms=(target-now).TotalMilliseconds;var sec=(long)Math.Ceiling(Math.Abs(ms)/1000);if(ms==0)return "切替予定時刻です";if(ms>0)return ms<60000?$"あと{sec}秒":$"あと{(sec+59)/60}分";return sec<60?$"{sec}秒遅れ中":$"{sec/60}分{sec%60:00}秒遅れ中";}
}
