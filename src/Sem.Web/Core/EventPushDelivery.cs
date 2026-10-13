using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
using WebPush;
namespace Sem.Web.Core;
public sealed class EventPushDelivery {
 public string Id{get;set;}="";public string EventId{get;set;}="";public string EntryId{get;set;}="";public string Kind{get;set;}="";
 public string UserId{get;set;}="";public string DeviceId{get;set;}="";public string DeviceFingerprint{get;set;}="";public string SourceFingerprint{get;set;}="";
 public long RuleVersion{get;set;}public long Due{get;set;}public long Expires{get;set;}public string Status{get;set;}="Pending";
 public int Attempts{get;set;}public string?Claim{get;set;}public long?LeaseUntil{get;set;}public long?AcceptedAt{get;set;}public string?Error{get;set;}
}
public sealed record EventPushPayload(string Title,string Body,string Url,string Id,long ExpiresAt);
public interface IEventPushTransport {bool Configured{get;}Task Send(PushDevice device,EventPushPayload payload,int ttl,CancellationToken ct);}
public sealed class EventWebPushTransport: IEventPushTransport,IDisposable {
 readonly WebPushClient client;readonly IConfiguration configuration;
 public EventWebPushTransport(HttpClient http,IConfiguration configuration){client=new(http);this.configuration=configuration;}
 public bool Configured=>!string.IsNullOrWhiteSpace(configuration["Push:PublicKey"])&&!string.IsNullOrWhiteSpace(configuration["Push:PrivateKey"]);
 public Task Send(PushDevice device,EventPushPayload payload,int ttl,CancellationToken ct){
  if(!Configured||ttl<1||!PushBinding.AllowedEndpoint(device.Endpoint))throw new InvalidOperationException("Push transport unavailable");
  client.SetVapidDetails(new VapidDetails(configuration["Push:Subject"]??"mailto:misc@raub.jp",configuration["Push:PublicKey"]!,configuration["Push:PrivateKey"]!));
  return client.SendNotificationAsync(new PushSubscription(device.Endpoint,device.P256dh,device.Auth),JsonSerializer.Serialize(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web)),new Dictionary<string,object>{{"TTL",ttl},{"headers",new Dictionary<string,object>{{"Topic",payload.Id[..32]}}}},ct);
 }
 public void Dispose()=>client.Dispose();
}
public static class PushBinding {
 public static bool AllowedEndpoint(string endpoint)=>Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Port==443&&uri.UserInfo.Length==0&&uri.Fragment.Length==0&&new[]{"web.push.apple.com","fcm.googleapis.com","updates.push.services.mozilla.com"}.Contains(uri.Host);
 public static string Fingerprint(PushDevice d)=>SemEndpoints.Hash(JsonSerializer.Serialize(new{d.Id,d.UserId,d.Endpoint,d.P256dh,d.Auth,d.SecurityStamp,d.SessionHash}));
 public static async Task<bool> Valid(ApplicationDbContext db,ApplicationUser user,PushDevice d,CancellationToken ct)=>user.Enabled&&user.EmailConfirmed&&!string.IsNullOrEmpty(user.SecurityStamp)&&d.UserId==user.Id&&d.SecurityStamp==user.SecurityStamp&&AllowedEndpoint(d.Endpoint)&&!string.IsNullOrWhiteSpace(d.P256dh)&&!string.IsNullOrWhiteSpace(d.Auth)&&(!string.IsNullOrEmpty(d.SessionHash)&&!await db.RevokedAuthSessions.AnyAsync(x=>x.TokenHash==d.SessionHash,ct));
 public static Task<int> RevokeSession(ApplicationDbContext db,string userId,string hash,CancellationToken ct=default)=>db.PushDevices.Where(x=>x.UserId==userId&&(x.SessionHash==hash||x.SessionHash==null)).ExecuteDeleteAsync(ct);
}
public sealed partial class CoreFoundationService {
 internal static bool PushViewAllowed(SessionEvent e,ApplicationUser user,DateTimeOffset now){
  if(!user.Enabled||!user.EmailConfirmed||e.Phases.Count(x=>x.IsActive)!=1)return false;
  if(user.IsAdmin||e.Admins.Any(x=>x.UserId==user.Id))return true;
  var phase=e.ManualPhaseOverride?e.Phases.Single(x=>x.IsActive):Automatic(e,now);return phase.DeletedAt==null&&phase.Kind is "View" or "Custom";
 }
 internal static async Task<string[]> PushRecipients(ApplicationDbContext db,SessionEvent e,NotificationPlanEntry entry,ProgressRow[]rows,CancellationToken ct){
  string? sid=entry.Kind=="SongPreparation"?rows.SingleOrDefault(x=>x.Original.Id==entry.PartId)?.Latest.SongId:null;
  if(entry.Kind=="SongPreparation"&&sid is null)return [];
  var performers=await (from p in db.SongParts join s in db.Songs on p.SongId equals s.Id join d in db.EventPartDefinitions on p.PartDefinitionId equals d.Id where p.EventId==e.Id&&s.DeletedAt==null&&d.DeletedAt==null&&p.AssignedUserId!=null&&(sid==null||p.SongId==sid) select p.AssignedUserId!).Distinct().ToListAsync(ct);
  if(entry.Kind=="OpeningReminder")performers.AddRange(await db.EventSpectators.Where(x=>x.EventId==e.Id).Select(x=>x.UserId).ToArrayAsync(ct));
  return performers.Distinct(StringComparer.Ordinal).ToArray();
 }
}
public sealed class EventPushScheduler(IDbContextFactory<ApplicationDbContext> factory,TimeProvider clock){
 public static string Id(string eventId,string entryId,string userId,string deviceId)=>SemEndpoints.Hash(JsonSerializer.Serialize(new[]{"event-push",eventId,entryId,userId,deviceId}));
 public async Task<(string Cursor,int Failed)> Batch(string cursor,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);var ids=await db.Set<EventPushRule>().Where(x=>x.Enabled&&x.EventId.CompareTo(cursor)>0).Select(x=>x.EventId).Concat(db.Set<PartStartPreference>().Where(x=>x.Enabled&&x.EventId.CompareTo(cursor)>0).Select(x=>x.EventId)).Distinct().OrderBy(x=>x).Take(20).ToArrayAsync(ct);if(ids.Length==0)return("",0);int failed=0;
  foreach(var id in ids)try{await Generate(id,ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{failed++;}
  return(ids[^1],failed);
 }
 public async Task Generate(string id,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var now=clock.GetUtcNow();
  var e=await db.SessionEvents.AsNoTracking().Include(x=>x.Admins).Include(x=>x.Phases).SingleOrDefaultAsync(x=>x.Id==id,ct);if(e is null)return;
  var source=await CoreFoundationService.BuildNotificationSource(db,e,now,ct);var rules=await db.Set<EventPushRule>().AsNoTracking().Where(x=>x.EventId==id&&x.Enabled).ToArrayAsync(ct);
  foreach(var entry in source.Plan.Entries){var rule=rules.SingleOrDefault(x=>x.Kind==entry.Kind);var eligible=EventPushRulePolicy.Evaluate(entry,rule,source.Plan.ProgressVersion,source.Rows,now);if(eligible.State!="Eligible")continue;
   var recipients=await CoreFoundationService.PushRecipients(db,e,entry,source.Rows,ct);
   var users=await db.Users.AsNoTracking().Where(x=>recipients.Contains(x.Id)&&x.Enabled&&x.EmailConfirmed).ToArrayAsync(ct);
   foreach(var user in users){if(!CoreFoundationService.PushViewAllowed(e,user,now))continue;
    var devices=await db.PushDevices.AsNoTracking().Where(x=>x.UserId==user.Id).ToArrayAsync(ct);
    foreach(var device in devices){if(!await PushBinding.Valid(db,user,device,ct))continue;var key=Id(id,entry.Id,user.Id,device.Id);var fingerprint=PushBinding.Fingerprint(device);
     var item=await db.Set<EventPushDelivery>().SingleOrDefaultAsync(x=>x.Id==key,ct);
     if(item is not null&&(item.Status is "Accepted" or "Sending"||item.SourceFingerprint==source.Plan.Fingerprint&&item.RuleVersion==rule!.Version&&item.DeviceFingerprint==fingerprint))continue;
     if(item is null){item=new(){Id=key,EventId=id,EntryId=entry.Id,Kind=entry.Kind,UserId=user.Id,DeviceId=device.Id};db.Add(item);}
     item.Status="Pending";item.Attempts=0;item.Claim=null;item.LeaseUntil=null;item.Error=null;item.SourceFingerprint=source.Plan.Fingerprint;item.DeviceFingerprint=fingerprint;item.RuleVersion=rule!.Version;item.Due=eligible.DueAt!.Value.ToUnixTimeMilliseconds();item.Expires=eligible.ExpiresAt!.Value.ToUnixTimeMilliseconds();
    }
   }
  }
  var personal=await db.Set<PartStartPreference>().AsNoTracking().Where(x=>x.EventId==id&&x.Enabled).ToArrayAsync(ct);
  if(personal.Length>0){var actual=await CoreFoundationService.BuildPartStartSource(db,e,now,ct);
   foreach(var preference in personal){var candidate=actual.Candidates.SingleOrDefault(x=>x.PartId==preference.PartId);if(PartStartDeliveryPolicy.Evaluate(candidate,preference,actual.ProgressVersion,now)!="Eligible")continue;
    var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==preference.UserId,ct);if(user is null||!CoreFoundationService.PushViewAllowed(e,user,now))continue;
    foreach(var device in await db.PushDevices.AsNoTracking().Where(x=>x.UserId==user.Id).ToArrayAsync(ct)){if(!await PushBinding.Valid(db,user,device,ct))continue;var entry=PartStartDeliveryPolicy.Entry(preference.PartId);var key=Id(id,entry,user.Id,device.Id);var fingerprint=PushBinding.Fingerprint(device);var item=await db.Set<EventPushDelivery>().SingleOrDefaultAsync(x=>x.Id==key,ct);
     if(item is not null&&(item.Status is "Accepted" or "Sending"||item.SourceFingerprint==candidate!.Fingerprint&&item.RuleVersion==preference.Version&&item.DeviceFingerprint==fingerprint))continue;
     if(item is null){item=new(){Id=key,EventId=id,EntryId=entry,Kind="PartStart",UserId=user.Id,DeviceId=device.Id};db.Add(item);}
     item.Status="Pending";item.Attempts=0;item.Claim=null;item.LeaseUntil=null;item.Error=null;item.SourceFingerprint=candidate!.Fingerprint;item.DeviceFingerprint=fingerprint;item.RuleVersion=preference.Version;item.Due=candidate.Started!.Value.ToUnixTimeMilliseconds();item.Expires=candidate.Expires!.Value.ToUnixTimeMilliseconds();
    }
   }
  }
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
 }
}
public sealed class EventPushDispatcher(IDbContextFactory<ApplicationDbContext> factory,IEventPushTransport transport,TimeProvider clock,ILogger<EventPushDispatcher> logger){
 const int Limit=5;const long LeaseMs=120000;
 public async Task Dispatch(CancellationToken ct=default){
  if(!transport.Configured)return;await using var db=await factory.CreateDbContextAsync(ct);var now=clock.GetUtcNow().ToUnixTimeMilliseconds();
  await db.Set<EventPushDelivery>().Where(x=>x.Status=="Sending"&&x.Attempts>=Limit&&x.LeaseUntil<=now).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Failed").SetProperty(x=>x.Error,"LeaseExpired").SetProperty(x=>x.Claim,(string?)null).SetProperty(x=>x.LeaseUntil,(long?)null),ct);
  var ids=await db.Set<EventPushDelivery>().AsNoTracking().Where(x=>x.Attempts<Limit&&x.Due<=now&&(x.Status=="Pending"||x.Status=="Sending"&&x.LeaseUntil<=now)).OrderBy(x=>x.Due).ThenBy(x=>x.Id).Select(x=>x.Id).Take(20).ToArrayAsync(ct);
  foreach(var id in ids){ct.ThrowIfCancellationRequested();now=clock.GetUtcNow().ToUnixTimeMilliseconds();var claim=Guid.NewGuid().ToString();
   var updated=await db.Set<EventPushDelivery>().Where(x=>x.Id==id&&x.Attempts<Limit&&x.Due<=now&&(x.Status=="Pending"||x.Status=="Sending"&&x.LeaseUntil<=now)).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Sending").SetProperty(x=>x.Claim,claim).SetProperty(x=>x.LeaseUntil,now+LeaseMs).SetProperty(x=>x.Attempts,x=>x.Attempts+1),ct);if(updated!=1)continue;
   var item=await db.Set<EventPushDelivery>().AsNoTracking().SingleAsync(x=>x.Id==id&&x.Claim==claim,ct);
   try{
    var prepared=await Prepare(item,ct);if(prepared is null){await Finish(db,item,claim,"Cancelled","CurrentGateRejected",null,ct);continue;}
    // Renew owned lease, then re-read ALL permission/source/device/expiry gates immediately before send.
    updated=await db.Set<EventPushDelivery>().Where(x=>x.Id==id&&x.Status=="Sending"&&x.Claim==claim&&x.LeaseUntil>clock.GetUtcNow().ToUnixTimeMilliseconds()).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LeaseUntil,clock.GetUtcNow().ToUnixTimeMilliseconds()+LeaseMs),ct);if(updated!=1)continue;
    prepared=await Prepare(item,ct);if(prepared is null){await Finish(db,item,claim,"Cancelled","FinalGateRejected",null,ct);continue;}
    var remaining=prepared.Value.Expires-clock.GetUtcNow().ToUnixTimeMilliseconds();var ttl=(int)Math.Min(int.MaxValue,remaining/1000);if(ttl<1){await Finish(db,item,claim,"Cancelled","Expired",null,ct);continue;}
    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(15000,remaining)));
    await transport.Send(prepared.Value.Device,prepared.Value.Payload,ttl,timeout.Token);
    await Finish(db,item,claim,"Accepted",null,prepared.Value.Payload,ct);
   }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
   catch(WebPushException ex)when((int)ex.StatusCode is 404 or 410){var current=await db.PushDevices.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==item.DeviceId,ct);if(current is not null&&PushBinding.Fingerprint(current)==item.DeviceFingerprint)await db.PushDevices.Where(x=>x.Id==current.Id&&x.Endpoint==current.Endpoint&&x.P256dh==current.P256dh&&x.Auth==current.Auth&&x.SessionHash==current.SessionHash&&x.SecurityStamp==current.SecurityStamp).ExecuteDeleteAsync(ct);await Finish(db,item,claim,"Cancelled","SubscriptionGone",null,ct);}
   catch(Exception ex){var retry=clock.GetUtcNow().ToUnixTimeMilliseconds()+(long)Math.Pow(2,item.Attempts)*15000;await db.Set<EventPushDelivery>().Where(x=>x.Id==id&&x.Status=="Sending"&&x.Claim==claim).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,item.Attempts>=Limit?"Failed":"Pending").SetProperty(x=>x.Due,retry).SetProperty(x=>x.Error,ex.GetType().Name).SetProperty(x=>x.Claim,(string?)null).SetProperty(x=>x.LeaseUntil,(long?)null),ct);logger.LogWarning("Event Push attempt failed; attempt={Attempt}, type={Type}",item.Attempts,ex.GetType().Name);}
  }
 }
 async Task<(PushDevice Device,EventPushPayload Payload,long Expires)?> Prepare(EventPushDelivery item,CancellationToken ct){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var now=clock.GetUtcNow();
  var e=await db.SessionEvents.AsNoTracking().Include(x=>x.Admins).Include(x=>x.Phases).SingleOrDefaultAsync(x=>x.Id==item.EventId,ct);var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==item.UserId,ct);var device=await db.PushDevices.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==item.DeviceId,ct);
  if(e is null||user is null||device is null||!CoreFoundationService.PushViewAllowed(e,user,now)||!await PushBinding.Valid(db,user,device,ct)||PushBinding.Fingerprint(device)!=item.DeviceFingerprint)return null;
  if(item.Kind=="PartStart"){
   if(!item.EntryId.StartsWith("part-start:",StringComparison.Ordinal))return null;var partId=item.EntryId["part-start:".Length..];var preference=await db.Set<PartStartPreference>().AsNoTracking().SingleOrDefaultAsync(x=>x.EventId==e.Id&&x.UserId==user.Id&&x.PartId==partId,ct);
   if(preference is null||preference.Version!=item.RuleVersion)return null;var actual=await CoreFoundationService.BuildPartStartSource(db,e,now,ct);var candidate=actual.Candidates.SingleOrDefault(x=>x.PartId==partId);
   if(PartStartDeliveryPolicy.Evaluate(candidate,preference,actual.ProgressVersion,now)!="Eligible"||candidate!.Fingerprint!=item.SourceFingerprint)return null;
   var personalExpires=candidate.Expires!.Value.ToUnixTimeMilliseconds();var personalBody="実開始日時："+candidate.Started!.Value.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy/MM/dd HH:mm:ss zzz")+"。最新の情報はアプリで確認してください。";var personalTag=SemEndpoints.Hash(JsonSerializer.Serialize(new[]{"event-push-user",e.Id,item.EntryId,user.Id}));await tx.CommitAsync(ct);return(device,new("パート開始のお知らせ",personalBody,"/events/"+Uri.EscapeDataString(e.Id)+"/timetable",personalTag,personalExpires),personalExpires);
  }
  var rule=await db.Set<EventPushRule>().AsNoTracking().SingleOrDefaultAsync(x=>x.EventId==e.Id&&x.Kind==item.Kind,ct);if(rule is null||!rule.Enabled||rule.Version!=item.RuleVersion)return null;
  var source=await CoreFoundationService.BuildNotificationSource(db,e,now,ct);if(source.Plan.Fingerprint!=item.SourceFingerprint)return null;var entry=source.Plan.Entries.SingleOrDefault(x=>x.Id==item.EntryId);if(entry is null||entry.Kind!=item.Kind)return null;
  var gate=EventPushRulePolicy.Evaluate(entry,rule,source.Plan.ProgressVersion,source.Rows,now);if(gate.State!="Eligible"||! (await CoreFoundationService.PushRecipients(db,e,entry,source.Rows,ct)).Contains(user.Id))return null;
  var expires=gate.ExpiresAt!.Value.ToUnixTimeMilliseconds();if(expires<=now.ToUnixTimeMilliseconds())return null;
  var display=(entry.Kind=="OpeningReminder"?entry.ReferenceAt:entry.DueAt)!.Value.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy/MM/dd HH:mm:ss zzz");
  var title=entry.Kind=="OpeningReminder"?"開催前のお知らせ":"曲の準備のお知らせ";var body=(entry.Kind=="OpeningReminder"?"開催基準日時：":"情報更新日時：")+display+"。最新の情報はアプリで確認してください。";
  var url="/events/"+Uri.EscapeDataString(e.Id)+"/timetable";var tag=SemEndpoints.Hash(JsonSerializer.Serialize(new[]{"event-push-user",e.Id,entry.Id,user.Id}));
  await tx.CommitAsync(ct);return(device,new(title,body,url,tag,expires),expires);
 }
 async Task Finish(ApplicationDbContext db,EventPushDelivery item,string claim,string status,string?error,EventPushPayload?payload,CancellationToken ct){
  await using var tx=await db.Database.BeginTransactionAsync(ct);long?accepted=status=="Accepted"?clock.GetUtcNow().ToUnixTimeMilliseconds():null;
  var changed=await db.Set<EventPushDelivery>().Where(x=>x.Id==item.Id&&x.Status=="Sending"&&x.Claim==claim).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,status).SetProperty(x=>x.AcceptedAt,accepted).SetProperty(x=>x.Error,error).SetProperty(x=>x.Claim,(string?)null).SetProperty(x=>x.LeaseUntil,(long?)null),ct);
  if(changed==1&&payload is not null){await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO Messages (Id,UserId,Title,Body,Created,ReadAt) VALUES ({payload.Id},{item.UserId},{payload.Title},{payload.Body},{clock.GetUtcNow().ToUnixTimeSeconds()},NULL)",ct);}
  await tx.CommitAsync(ct);
 }
}
public sealed class EventPushWorker(IServiceScopeFactory scopes,ILogger<EventPushWorker> logger):BackgroundService{
 protected override async Task ExecuteAsync(CancellationToken ct){string cursor="";using var timer=new PeriodicTimer(TimeSpan.FromSeconds(3));while(await timer.WaitForNextTickAsync(ct)){
  try{await using var scope=scopes.CreateAsyncScope();if(!scope.ServiceProvider.GetRequiredService<IEventPushTransport>().Configured)continue;var batch=await scope.ServiceProvider.GetRequiredService<EventPushScheduler>().Batch(cursor,ct);cursor=batch.Cursor;if(batch.Failed>0)logger.LogWarning("Event Push scheduling skipped {Count} invalid events",batch.Failed);await scope.ServiceProvider.GetRequiredService<EventPushDispatcher>().Dispatch(ct);}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}catch(Exception ex){logger.LogWarning("Event Push worker failed; type={Type}",ex.GetType().Name);}
 }}
}
