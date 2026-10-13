using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
public sealed class DailyDigestRun {
 public string Day{get;set;}="";public long CreatedAt{get;set;}
}
public sealed class DailyDigestMail {
 public string Id{get;set;}="";public string Day{get;set;}="";public string UserId{get;set;}="";
 public long Due{get;set;}public string Status{get;set;}="Pending";public int Attempts{get;set;}
 public string?Claim{get;set;}public long?LeaseUntil{get;set;}public long?SentAt{get;set;}public string?Error{get;set;}
}
public static class DailyDigestPolicy {
 public static DateTimeOffset ScheduledAt(DateOnly day)=>DigestPreviewBuilder.Bounds(day).End.AddMinutes(5);
 public static DateOnly LastClosedDay(DateTimeOffset now){var japan=now.ToOffset(TimeSpan.FromHours(9));return DateOnly.FromDateTime(japan.DateTime).AddDays(japan.TimeOfDay>=TimeSpan.FromMinutes(5)?-1:-2);}
 public static string MailId(string day,string userId)=>SemEndpoints.Hash("daily-digest:"+day+":"+userId);
}
public sealed class DailyDigestScheduler(IDbContextFactory<ApplicationDbContext> factory,TimeProvider clock){
 public async Task Generate(CancellationToken ct=default){
  var closed=DailyDigestPolicy.LastClosedDay(clock.GetUtcNow());var cutoff=DigestPreviewBuilder.Format(DigestPreviewBuilder.Bounds(closed).End);
  await using var discovery=await factory.CreateDbContextAsync(ct);
  // Only recorded closed days need work. Empty days never create mail. Catch-up is bounded per tick, not truncated.
  var days=await discovery.Database.SqlQuery<string>($"""
   SELECT DISTINCT date(n."CreatedAt",'+9 hours') AS "Value" FROM "BusinessNotifications" n
   WHERE n."CreatedAt" < {cutoff} AND NOT EXISTS(SELECT 1 FROM "DailyDigestRuns" r WHERE r."Day"=date(n."CreatedAt",'+9 hours'))
   ORDER BY "Value" LIMIT 7
   """).ToListAsync(ct);
  foreach(var text in days){
   var day=DateOnly.ParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture);var(start,end)=DigestPreviewBuilder.Bounds(day);var lower=DigestPreviewBuilder.Format(start);var upper=DigestPreviewBuilder.Format(end);
   await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
   if(await db.DailyDigestRuns.AnyAsync(x=>x.Day==text,ct))continue;
   db.DailyDigestRuns.Add(new(){Day=text,CreatedAt=clock.GetUtcNow().ToUnixTimeSeconds()});await db.SaveChangesAsync(ct);
   var events=await db.BusinessNotifications.Where(x=>x.CreatedAt.CompareTo(lower)>=0&&x.CreatedAt.CompareTo(upper)<0).Select(x=>x.EventId).Distinct().ToArrayAsync(ct);
   var recipients=await db.Users.Where(u=>u.Enabled&&u.EmailConfirmed&&u.Email!=null&&u.Email!=""&&(u.IsAdmin||db.EventAdmins.Any(a=>a.UserId==u.Id&&events.Contains(a.EventId)))).Select(u=>u.Id).ToArrayAsync(ct);
   if(events.Length>0)foreach(var userId in recipients)db.DailyDigestMails.Add(new(){Id=DailyDigestPolicy.MailId(text,userId),Day=text,UserId=userId,Due=DailyDigestPolicy.ScheduledAt(day).ToUnixTimeSeconds()});
   await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
  }
 }
}
public sealed class DailyDigestDispatcher(IDbContextFactory<ApplicationDbContext> factory,BusinessNotificationStore store,
 IBusinessEmailTransport transport,TimeProvider clock,ILogger<DailyDigestDispatcher> logger){
 const int MaxAttempts=5,LeaseSeconds=120;
 public async Task Dispatch(CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);var now=clock.GetUtcNow().ToUnixTimeSeconds();
  await db.DailyDigestMails.Where(x=>x.Status=="Sending"&&x.Attempts>=MaxAttempts&&x.LeaseUntil<=now).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Failed").SetProperty(x=>x.Error,"LeaseExpired").SetProperty(x=>x.Claim,(string?)null).SetProperty(x=>x.LeaseUntil,(long?)null),ct);
  var ids=await db.DailyDigestMails.AsNoTracking().Where(x=>x.Attempts<MaxAttempts&&x.Due<=now&&(x.Status=="Pending"||x.Status=="Sending"&&x.LeaseUntil<=now)).OrderBy(x=>x.Day).ThenBy(x=>x.Id).Select(x=>x.Id).Take(20).ToArrayAsync(ct);
  foreach(var id in ids){
   ct.ThrowIfCancellationRequested();now=clock.GetUtcNow().ToUnixTimeSeconds();var claim=Guid.NewGuid().ToString();
   var changed=await db.DailyDigestMails.Where(x=>x.Id==id&&x.Attempts<MaxAttempts&&x.Due<=now&&(x.Status=="Pending"||x.Status=="Sending"&&x.LeaseUntil<=now)).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Sending").SetProperty(x=>x.Claim,claim).SetProperty(x=>x.LeaseUntil,now+LeaseSeconds).SetProperty(x=>x.Attempts,x=>x.Attempts+1),ct);
   if(changed!=1)continue;var item=await db.DailyDigestMails.AsNoTracking().SingleAsync(x=>x.Id==id&&x.Claim==claim,ct);
   try{
    var prepared=await Prepare(item,ct);
    if(prepared is null){await Complete(db,id,claim,"Cancelled","NoAuthorizedChanges",false,ct);continue;}
    // Final ownership and fresh authorization checks precede the external side effect.
    var scopeNow=await ScopeSignature(item.UserId,ct);
    if(scopeNow!=prepared.Value.Scope)throw new InvalidOperationException("Digest authorization changed during preparation");
    var renewed=await db.DailyDigestMails.Where(x=>x.Id==id&&x.Status=="Sending"&&x.Claim==claim&&x.LeaseUntil>clock.GetUtcNow().ToUnixTimeSeconds())
     .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.LeaseUntil,clock.GetUtcNow().ToUnixTimeSeconds()+LeaseSeconds),ct);
    if(renewed!=1)continue;
    await transport.Send("daily-digest:"+item.Id,prepared.Value.User,prepared.Value.Payload,ct);
    await Complete(db,id,claim,"Sent",null,true,ct);
   }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
   catch(Exception ex){
    var due=clock.GetUtcNow().ToUnixTimeSeconds()+(long)Math.Pow(2,item.Attempts)*15;
    await db.DailyDigestMails.Where(x=>x.Id==id&&x.Status=="Sending"&&x.Claim==claim).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,item.Attempts>=MaxAttempts?"Failed":"Pending").SetProperty(x=>x.Due,due).SetProperty(x=>x.Error,ex.GetType().Name).SetProperty(x=>x.Claim,(string?)null).SetProperty(x=>x.LeaseUntil,(long?)null),ct);
    logger.LogWarning("Daily digest failed; attempt={Attempt}, type={Type}",item.Attempts,ex.GetType().Name);
   }
  }
 }
 async Task<(ApplicationUser User,BusinessEmailPayload Payload,string Scope)?>Prepare(DailyDigestMail mail,CancellationToken ct){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
  var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==mail.UserId,ct);
  if(user is null||!user.Enabled||!user.EmailConfirmed||string.IsNullOrWhiteSpace(user.Email))return null;
  var allowed=user.IsAdmin?null:await db.EventAdmins.Where(x=>x.UserId==user.Id).Select(x=>x.EventId).ToArrayAsync(ct);
  if(allowed is{Length:0})return null;
  var day=DateOnly.ParseExact(mail.Day,"yyyy-MM-dd",CultureInfo.InvariantCulture);var(start,end)=DigestPreviewBuilder.Bounds(day);var lower=DigestPreviewBuilder.Format(start);var upper=DigestPreviewBuilder.Format(end);
  var query=db.BusinessNotifications.AsNoTracking().Where(x=>x.CreatedAt.CompareTo(lower)>=0&&x.CreatedAt.CompareTo(upper)<0);
  if(allowed is not null)query=query.Where(x=>allowed.Contains(x.EventId));
  var entries=new List<DigestEntry>();string?after=null;
  // SMTP's one-message rule is respected without borrowing the preview's 1000-row cutoff.
  while(true){
   var batch=await query.Where(x=>after==null||x.Id.CompareTo(after)>0).OrderBy(x=>x.Id).Take(500).ToArrayAsync(ct);if(batch.Length==0)break;
   var people=batch.Select(x=>x.UserId).Distinct().ToArray();var names=await db.Users.AsNoTracking().Where(x=>people.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,x=>x.DisplayName,ct);
   foreach(var record in batch){var p=store.Read(record)??throw new InvalidOperationException("Invalid digest snapshot");entries.Add(new(record.Id,record.EventId,p.EventTitle,record.Kind,record.CreatedAt,record.UserId,names.GetValueOrDefault(record.UserId,"（表示名を確認できません）"),p.ActorName,p.Reason,p.Slots));}
   after=batch[^1].Id;
  }
  if(entries.Count==0)return null;
  var name=await db.ServiceSettings.Select(x=>x.Name).SingleOrDefaultAsync(ct)??await db.SiteStates.Select(x=>x.Name).SingleOrDefaultAsync(ct)??"SEM";
  var result=DigestPreviewBuilder.Build(day,name,entries,preview:false);await tx.CommitAsync(ct);
  // No body is persisted in this queue: retries rebuild and recheck current permissions.
  return(user,new BusinessEmailPayload("",mail.Day,"","",[],result.Subject,result.Body),Signature(user,allowed));
 }
 static string Signature(ApplicationUser user,string[]?allowed)=>SemEndpoints.Hash(System.Text.Json.JsonSerializer.Serialize(new{user.Enabled,user.EmailConfirmed,user.Email,user.IsAdmin,events=allowed?.Order(StringComparer.Ordinal).ToArray()}));
 async Task<string?>ScopeSignature(string userId,CancellationToken ct){
  await using var db=await factory.CreateDbContextAsync(ct);var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==userId,ct);if(user is null)return null;
  var allowed=user.IsAdmin?null:await db.EventAdmins.Where(x=>x.UserId==user.Id).Select(x=>x.EventId).ToArrayAsync(ct);return Signature(user,allowed);
 }
 async Task Complete(ApplicationDbContext db,string id,string claim,string status,string?error,bool sent,CancellationToken ct){
  long?at=sent?clock.GetUtcNow().ToUnixTimeSeconds():null;
  await db.DailyDigestMails.Where(x=>x.Id==id&&x.Status=="Sending"&&x.Claim==claim).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,status).SetProperty(x=>x.Error,error).SetProperty(x=>x.SentAt,at).SetProperty(x=>x.Claim,(string?)null).SetProperty(x=>x.LeaseUntil,(long?)null),ct);
 }
}
public sealed class DailyDigestWorker(IServiceScopeFactory scopes,ILogger<DailyDigestWorker> logger):BackgroundService{
 protected override async Task ExecuteAsync(CancellationToken ct){using var timer=new PeriodicTimer(TimeSpan.FromSeconds(3));while(await timer.WaitForNextTickAsync(ct)){
  try{await using var scope=scopes.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<DailyDigestScheduler>().Generate(ct);await scope.ServiceProvider.GetRequiredService<DailyDigestDispatcher>().Dispatch(ct);}
  catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
  catch(Exception ex){logger.LogWarning("Daily digest iteration failed; type={Type}",ex.GetType().Name);}
 }}
}
