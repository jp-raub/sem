using System.ComponentModel.DataAnnotations;
using System.Globalization;
namespace Sem.Web.Core;
public sealed record TimedPart(string Id,string Kind,string Title,string Artist,string Icon,string? SongId,DateTimeOffset? PrepStart,DateTimeOffset Start,DateTimeOffset End,int LengthSeconds,int? ConfiguredPrepSeconds);
public sealed record CalculatedTimetable(TimedPart[] Rows,DateTimeOffset End,long OverrunSeconds);
public static class TimetableCalculator {
 public static readonly string[] Icons=["🎵","💿","🎤"];
 public static DateTimeOffset Instant(string value)=>DateTimeOffset.Parse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();
 private static DateTimeOffset CeilMinute(DateTimeOffset value){var ticks=value.UtcTicks;var remainder=ticks%TimeSpan.TicksPerMinute;return new DateTimeOffset(remainder==0?ticks:checked(ticks+TimeSpan.TicksPerMinute-remainder),TimeSpan.Zero);}
 public static CalculatedTimetable Calculate(SessionEvent e,IEnumerable<TimePart> parts,IEnumerable<Song> songs){
  try {
   var cursor=Instant(e.ScheduledDate);var rows=new List<TimedPart>();var byId=songs.Where(s=>s.DeletedAt==null).ToDictionary(s=>s.Id);
   if(e.OpensAt is string opens){var opening=Instant(opens);if(opening>cursor)throw new ValidationException("開場は開始以前にしてください。");rows.Add(new("opening","Opening","開場・受付開始","","📄",null,null,opening,cursor,checked((int)(cursor-opening).TotalSeconds),null));}
   foreach(var p in parts.Where(p=>p.DeletedAt==null).OrderBy(p=>p.SortOrder).ThenBy(p=>p.Id)) {
    if(p.Kind=="Song"){
     if(p.SongId is null||!byId.TryGetValue(p.SongId,out var s))throw new ValidationException("曲とタイムパートの参照を確認してください。");
     var prep=s.PrepSeconds??e.DefaultPrepSeconds;if(prep<0||s.PerformanceSeconds<1)throw new ValidationException("曲の秒数を確認してください。");
     var begin=e.RoundStarts?CeilMinute(cursor):cursor;var start=begin.AddSeconds(prep);if(e.RoundStarts)start=CeilMinute(start);cursor=start.AddSeconds(s.PerformanceSeconds);
     rows.Add(new(p.Id,"Song",s.Title,s.Artist,p.Icon??e.SongIcon,s.Id,begin,start,cursor,s.PerformanceSeconds,prep));
    }else if(p.Kind=="Transition"&&p.DurationSeconds>=0){var begin=cursor;cursor=cursor.AddSeconds(p.DurationSeconds);rows.Add(new(p.Id,"Transition",p.Name,"","📄",null,null,begin,cursor,p.DurationSeconds,null));}
    else throw new ValidationException("タイムパートの種類と秒数を確認してください。");
   }
   return new(rows.ToArray(),cursor,e.PlannedEndAt is string limit?Math.Max(0,(long)Math.Ceiling((cursor-Instant(limit)).TotalSeconds)):0);
  }catch(Exception x)when(x is FormatException or OverflowException or ArgumentOutOfRangeException){throw new ValidationException("日時・所要時間が計算可能な範囲を超えています。");}
 }
 public static string Clock(DateTimeOffset value,string format)=>value.ToOffset(TimeSpan.FromHours(9)).ToString(format=="minutes"?"yyyy/MM/dd HH:mm":"yyyy/MM/dd HH:mm:ss",CultureInfo.InvariantCulture);
 public static string Duration(int value,string format){if(format=="colon")return SongDuration.Format(value);var m=value/60;var s=value%60;return s>0?$"{m}分{s:00}秒":m>=60?$"{m/60}時間{m%60:00}分":$"{m}分";}
}
