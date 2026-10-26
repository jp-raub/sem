using System.Globalization;
namespace Sem.Web.Core;
public static class SongDuration {
 public static string Format(int seconds)=>seconds>=3600?$"{seconds/3600}:{seconds/60%60:00}:{seconds%60:00}":$"{seconds/60}:{seconds%60:00}";
 public static bool TryParse(string? text,out int seconds){seconds=0;var fields=(text??"").Trim().Split(':');if(fields.Length is not (2 or 3)||fields.Any(x=>x.Length==0||x.Any(c=>c<'0'||c>'9')))return false;var n=new long[fields.Length];for(var i=0;i<n.Length;i++)if(!long.TryParse(fields[i],NumberStyles.None,CultureInfo.InvariantCulture,out n[i]))return false;if(n[^1]>59||n.Length==3&&n[1]>59)return false;if(n[0]>int.MaxValue/60)return false;var sum=n.Length==2?n[0]*60+n[1]:n[0]*3600+n[1]*60+n[2];if(sum>int.MaxValue)return false;seconds=(int)sum;return true;}
}
