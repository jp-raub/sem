using Sem.Web.Core;
namespace Sem.Web.Components.Shared;

// Read-only presentation of the existing server plan. Never changes marks/estimates.
public static class ProgressPresentation
{
 public static string Severity(long milliseconds) => milliseconds<=0?"none":milliseconds<60_000?"low":milliseconds<300_000?"medium":"high";
 public static long Delay(ProgressRow row,ProgressPoint point,ProgressNext? next,DateTimeOffset now)
 {
  var delta=(long)(point.Latest-point.Original).TotalMilliseconds;
  if(next?.PartId==row.Original.Id&&next.Point==point.Point&&point.Actual is null&&now>point.Latest)
   delta=Math.Max(delta,(long)(now-point.Original).TotalMilliseconds);
  return Math.Max(0,delta);
 }
 public static string DelayText(long milliseconds)
 {
  if(milliseconds<=0)return "";
  var seconds=(long)Math.Ceiling(milliseconds/1000d);
  return (seconds<60?$"{seconds}秒":$"{seconds/60}分{seconds%60:00}秒")+"遅れ";
 }
 public static string PointLabel(string kind,string point)=>point=="PrepStart"?"準備開始":point=="Start"?kind=="Song"?"演奏開始":kind=="Opening"?"受付開始":"進行開始":kind=="Song"?"演奏終了":"終了";
 public static string Duration(ProgressRow row)=>row.Original.Kind=="Song"?$"準備 {SongDuration.Format(row.Latest.ConfiguredPrepSeconds??0)} ／ 演奏 {SongDuration.Format(row.Latest.LengthSeconds)}":$"所要 {SongDuration.Format(row.Latest.LengthSeconds)}";
}
