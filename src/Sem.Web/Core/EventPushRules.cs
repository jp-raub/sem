using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
// Explicit arming does not connect a transport or make historical dormant entries sendable.
public sealed class EventPushRule {
 public string EventId {get;set;}=""; public string Kind {get;set;}="";
 public bool Enabled {get;set;} public long Version {get;set;}
 public long? ArmedAt {get;set;} public long? ArmedProgressVersion {get;set;} public long ChangedAt {get;set;}
}
public sealed record EventPushRuleView(string Kind,bool Enabled,long Version,DateTimeOffset? ArmedAt,long? ArmedProgressVersion);
public sealed record RuleEligibility(string Id,string Kind,string? PartId,string Title,string State,DateTimeOffset? DueAt,DateTimeOffset? ExpiresAt);
public sealed record EventPushRulesView(string EventId,string Title,long EventVersion,bool DeliveryConnected,EventPushRuleView[] Rules,RuleEligibility[] Entries);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventPushRuleEdit([property:JsonRequired]string Kind,[property:JsonRequired]bool Enabled,[property:JsonRequired]long Version,[property:JsonRequired]long EventVersion);
public static class EventPushRulePolicy {
 public static readonly string[] Kinds=["OpeningReminder","SongPreparation"];
 public static RuleEligibility Evaluate(NotificationPlanEntry entry,EventPushRule? rule,long progressVersion,ProgressRow[] rows,DateTimeOffset now){
  DateTimeOffset? expiry=null;
  if(entry.DueAt is DateTimeOffset due&&entry.ReferenceAt is DateTimeOffset reference){
   if(entry.Kind=="OpeningReminder")expiry=reference;
   else if(entry.Kind=="SongPreparation"){
    var row=rows.SingleOrDefault(x=>x.Original.Id==entry.PartId);if(row?.Latest.PrepStart is DateTimeOffset prep){var ten=due>DateTimeOffset.MaxValue.AddMinutes(-10)?DateTimeOffset.MaxValue:due.AddMinutes(10);expiry=ten<prep?ten:prep;}
   }
  }
  string state;
  if(rule is null||!rule.Enabled)state="Disabled";
  else if(rule.ArmedAt is null||rule.ArmedProgressVersion is null||rule.Kind!=entry.Kind||!Kinds.Contains(entry.Kind))state="InvalidRule";
  else if(entry.State is not ("Planned" or "Due" or "WaitingActual" or "Obsolete"))state="InvalidSource";
  else if(entry.State=="Obsolete")state="TargetStarted";
  else if(entry.DueAt is null)state="WaitingActual";
  else if(entry.DueAt.Value.ToUnixTimeMilliseconds()<=rule.ArmedAt||entry.Basis=="PreviousPartActualStart"&&progressVersion<=rule.ArmedProgressVersion)state="BeforeActivation";
  else if(expiry is null)state="InvalidExpiry";
  else if(now>=expiry)state="Expired";
  else state=now<entry.DueAt?"Planned":"Eligible";
  return new(entry.Id,entry.Kind,entry.PartId,entry.Title,state,entry.DueAt,expiry);
 }
}
public sealed partial class CoreFoundationService {
 public async Task<EventPushRulesView> ReadEventPushRulesAsync(ClaimsPrincipal principal,string id,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);var now=clock.GetUtcNow();
  var source=await BuildNotificationSource(db,e,now,ct);var rules=await db.Set<EventPushRule>().AsNoTracking().Where(x=>x.EventId==id).ToArrayAsync(ct);
  var view=new EventPushRulesView(id,e.Title,e.RowVersion,false,EventPushRulePolicy.Kinds.Select(kind=>{var rule=rules.SingleOrDefault(x=>x.Kind==kind);return new EventPushRuleView(kind,rule?.Enabled??false,rule?.Version??0,rule?.ArmedAt is long at?DateTimeOffset.FromUnixTimeMilliseconds(at):null,rule?.ArmedProgressVersion);}).ToArray(),source.Plan.Entries.Select(entry=>EventPushRulePolicy.Evaluate(entry,rules.SingleOrDefault(x=>x.Kind==entry.Kind),source.Plan.ProgressVersion,source.Rows,now)).ToArray());
  await tx.CommitAsync(ct);return view;
 }
 public async Task SaveEventPushRuleAsync(ClaimsPrincipal principal,string id,EventPushRuleEdit edit,CancellationToken ct=default){
  if(!EventPushRulePolicy.Kinds.Contains(edit.Kind)||edit.Version<0||edit.EventVersion<0)throw new ValidationException("通知種別と版を確認してください。");
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);var actor=await Actor(db,principal,ct);if(e.RowVersion!=edit.EventVersion)throw new CoreConflictException();
  // Invalid source cannot be armed and no audit/version is left behind.
  var source=await BuildNotificationSource(db,e,clock.GetUtcNow(),ct);var rule=await db.Set<EventPushRule>().SingleOrDefaultAsync(x=>x.EventId==id&&x.Kind==edit.Kind,ct);
  if((rule?.Version??0)!=edit.Version)throw new CoreConflictException();if((rule?.Enabled??false)==edit.Enabled){await tx.CommitAsync(ct);return;}
  if(rule is null){rule=new(){EventId=id,Kind=edit.Kind};db.Add(rule);}
  rule.Version++;rule.Enabled=edit.Enabled;rule.ChangedAt=clock.GetUtcNow().ToUnixTimeMilliseconds();rule.ArmedAt=edit.Enabled?rule.ChangedAt:null;rule.ArmedProgressVersion=edit.Enabled?source.Plan.ProgressVersion:null;
  Audit(db,actor.Id,"EventPushRuleChanged",id+":"+edit.Kind+":"+(edit.Enabled?"On":"Off"));
  try{await db.SaveChangesAsync(ct);}catch(DbUpdateException ex)when(ex.InnerException is Microsoft.Data.Sqlite.SqliteException sql&&(sql.SqliteErrorCode is 5 or 6||sql.SqliteExtendedErrorCode is 1555 or 2067)){throw new CoreConflictException();}
  await tx.CommitAsync(ct);
 }
}
