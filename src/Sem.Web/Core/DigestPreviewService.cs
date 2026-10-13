using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;

public sealed record DigestEntry(string OperationId,string EventId,string EventTitle,string Kind,string At,
 string SubjectUserId,string CurrentSubjectName,string ActorName,string Reason,BusinessSlot[] Slots);
public sealed record DigestPreview(string Day,string TimeZone,string StartUtc,string EndUtc,int OperationCount,
 int RegistrationSlots,int ReleaseSlots,string Subject,string Body,DigestEntry[] Entries);
public static class DigestPreviewBuilder {
 public static (DateTimeOffset Start,DateTimeOffset End) Bounds(DateOnly day){
  if(day==DateOnly.MinValue||day==DateOnly.MaxValue)throw new ValidationException("対象日を確認してください。");
  var start=new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue),TimeSpan.FromHours(9));return(start.ToUniversalTime(),start.AddDays(1).ToUniversalTime());
 }
 public static DigestPreview Build(DateOnly day,string serviceName,IEnumerable<DigestEntry> source){
  var(start,end)=Bounds(day);var entries=source.Where(x=>{
   if(!DateTimeOffset.TryParse(x.At,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var at))throw new ValidationException("操作履歴の日時を確認できません。");return at>=start&&at<end;
  }).OrderBy(x=>DateTimeOffset.Parse(x.At,CultureInfo.InvariantCulture)).ThenBy(x=>x.OperationId,StringComparer.Ordinal).ToArray();
  if(entries.Select(x=>x.OperationId).Distinct(StringComparer.Ordinal).Count()!=entries.Length||entries.Any(x=>x.Kind is not ("Register" or "Release")||x.Slots is null||x.Slots.Length==0||x.Slots.Any(s=>s is null)||x.Slots.Select(s=>s.SlotId).Distinct(StringComparer.Ordinal).Count()!=x.Slots.Length))throw new ValidationException("操作履歴の内容を確認できません。");
  var registered=entries.Where(x=>x.Kind=="Register").Sum(x=>x.Slots.Length);var released=entries.Where(x=>x.Kind=="Release").Sum(x=>x.Slots.Length);
  var date=day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);var subject=$"{serviceName.Replace('\r',' ').Replace('\n',' ')}：{date} 登録・解除ダイジェスト";
  var body=new StringBuilder().AppendLine($"対象日：{date}（Asia/Tokyo、日本時間00:00以上、翌日00:00未満）")
   .AppendLine($"確定操作：{entries.Length}件 ／ 登録：{registered}枠 ／ 解除：{released}枠")
   .AppendLine("確定済み操作の履歴です。本人メールの配信状況とは別で、登録と解除を相殺しません。")
   .AppendLine("対象者は現在の表示名です。イベント・曲・パート・操作者・理由は操作時の保存内容です。")
   .AppendLine("これは全体管理者向け文面プレビューです。送信予約・実送信は行いません。");
  foreach(var item in entries){
   body.AppendLine().AppendLine($"{DateTimeOffset.Parse(item.At,CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm:ss.fff zzz} ／ {(item.Kind=="Register"?"登録":"解除")}")
    .AppendLine($"イベント：{item.EventTitle}").AppendLine($"対象者（現在の表示名）：{item.CurrentSubjectName}").AppendLine($"操作者：{item.ActorName}");
   foreach(var slot in item.Slots)body.AppendLine($"曲：{slot.SongTitle} ／ パート：{slot.PartName}");
   if(item.Kind=="Release")body.AppendLine($"解除理由：{item.Reason}");
  }
  if(entries.Length==0)body.AppendLine("対象日の確定した登録・解除はありません。過去の操作を遡及作成しません。");
  return new(date,"Asia/Tokyo",Format(start),Format(end),entries.Length,registered,released,subject,body.ToString(),entries);
 }
 public static string Format(DateTimeOffset at)=>at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture);
}
public sealed partial class CoreFoundationService {
 public async Task<DigestPreview> ReadDigestPreviewAsync(ClaimsPrincipal principal,string? dayText,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var actor=await Actor(db,principal,ct);if(!actor.IsAdmin)throw new UnauthorizedAccessException("文面プレビューは全体管理者だけが利用できます。");
  DateOnly day;
  if(dayText is null)day=DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(9)).DateTime);
  else if(!DateOnly.TryParseExact(dayText,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out day))throw new ValidationException("対象日はyyyy-MM-ddで指定してください。");
  var(start,end)=DigestPreviewBuilder.Bounds(day);var lower=DigestPreviewBuilder.Format(start);var upper=DigestPreviewBuilder.Format(end);
  var records=await db.BusinessNotifications.AsNoTracking().Where(x=>x.CreatedAt.CompareTo(lower)>=0&&x.CreatedAt.CompareTo(upper)<0)
   .OrderBy(x=>x.CreatedAt).ThenBy(x=>x.Id).Take(1001).ToArrayAsync(ct);
  if(records.Length>1000)throw new ValidationException("プレビュー上限の1000操作を超えています。履歴を省略せず処理を中止しました。");
  var ids=records.Select(x=>x.UserId).Distinct().ToArray();var names=await db.Users.AsNoTracking().Where(x=>ids.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,x=>x.DisplayName,ct);
  var serviceName=await db.ServiceSettings.Select(x=>x.Name).SingleOrDefaultAsync(ct)??await db.SiteStates.Select(x=>x.Name).SingleOrDefaultAsync(ct)??"SEM";
  DigestEntry[] entries;
  try{entries=records.Select(x=>{var p=businessNotifications.Read(x)??throw new ValidationException("操作履歴の内容を確認できません。");return new DigestEntry(x.Id,x.EventId,p.EventTitle,x.Kind,x.CreatedAt,x.UserId,names.GetValueOrDefault(x.UserId,"（表示名を確認できません）"),p.ActorName,p.Reason,p.Slots);}).ToArray();}
  catch(Exception ex)when(ex is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or FormatException){throw new ValidationException("暗号化した操作履歴を確認できません。保護鍵とバックアップを確認してください。");}
  var result=DigestPreviewBuilder.Build(day,serviceName,entries);await tx.CommitAsync(ct);return result;
 }
}
