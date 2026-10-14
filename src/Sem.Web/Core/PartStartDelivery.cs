using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
public sealed record PartStartCandidate(string PartId,string Kind,DateTimeOffset?Started,DateTimeOffset?Ended,DateTimeOffset?Expires,string Fingerprint);
public static class PartStartDeliveryPolicy{
 public static string Evaluate(PartStartCandidate? candidate,PartStartPreference? preference,long progressVersion,DateTimeOffset now){
  if(preference is null||!preference.Enabled)return "Disabled";
  if(preference.ArmedAt is null||preference.ArmedProgressVersion is null)return "InvalidRule";
  if(candidate is null)return "PartRemoved";
  if(candidate.Started is null)return "WaitingActual";
  if(candidate.Started>now||candidate.Ended<candidate.Started)return "InvalidSource";
  if(candidate.Started.Value.ToUnixTimeMilliseconds()<=preference.ArmedAt||progressVersion<=preference.ArmedProgressVersion)return "BeforeActivation";
  if(candidate.Expires is null)return "InvalidSource";
  return now>=candidate.Expires?"Expired":"Eligible";
 }
 public static DateTimeOffset? Expiry(DateTimeOffset?start,DateTimeOffset?end){if(start is null)return null;var ten=start>DateTimeOffset.MaxValue.AddMinutes(-10)?DateTimeOffset.MaxValue:start.Value.AddMinutes(10);return end is not null&&end<ten?end:ten;}
 public static string Entry(string partId)=>"part-start:"+partId;
}
public sealed partial class CoreFoundationService{
 internal static async Task<(PartStartCandidate[]Candidates,long ProgressVersion)> BuildPartStartSource(ApplicationDbContext db,SessionEvent e,DateTimeOffset now,CancellationToken ct){
  // Current parts establish existence; frozen records establish actual Start/End. No guessed scheduled starts.
  var source=await BuildNotificationSource(db,e,now,ct);var table=await Timetable(db,e,true,ct);var marks=await db.ProgressMarks.AsNoTracking().Where(x=>x.EventId==e.Id).ToArrayAsync(ct);var version=source.Plan.ProgressVersion;
  return(table.Calculation.Rows.Select(row=>{var exists=source.Rows.Any(x=>x.Original.Id==row.Id&&x.Original.Kind==row.Kind);var start=exists?marks.SingleOrDefault(x=>x.PartId==row.Id&&x.Point=="Start"):null;var end=exists?marks.SingleOrDefault(x=>x.PartId==row.Id&&x.Point=="End"):null;DateTimeOffset?at=start is null?null:TimetableCalculator.Instant(start.At);DateTimeOffset?until=end is null?null:TimetableCalculator.Instant(end.At);var fp=SemEndpoints.Hash(JsonSerializer.Serialize(new{EventId=e.Id,PartId=row.Id,row.Kind,Start=start?.At,Operation=start?.OperationId}));return new PartStartCandidate(row.Id,row.Kind,at,until,PartStartDeliveryPolicy.Expiry(at,until),fp);}).ToArray(),version);
 }
}
