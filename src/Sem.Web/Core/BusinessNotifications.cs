using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

// One durable email per committed registration/release operation. No Push rules yet.
public sealed class BusinessNotification {
 public string Id {get;set;}="";
 public string EventId {get;set;}="";
 public string UserId {get;set;}="";
 public string Kind {get;set;}="";
 public string CreatedAt {get;set;}="";
 public string ProtectedPayload {get;set;}="";
 public long Due {get;set;}
 public string Status {get;set;}="Pending";
 public int Attempts {get;set;}
 public string? Claim {get;set;}
 public long? LeaseUntil {get;set;}
 public long? SentAt {get;set;}
 public string? Error {get;set;}
}
public sealed record BusinessSlot(string SlotId,string SongId,string SongTitle,string PartName);
public sealed record BusinessEmailPayload(string EventTitle,string ScheduledDate,string ActorName,string Reason,
 BusinessSlot[] Slots,string Subject,string Body);
public sealed class BusinessNotificationStore(IDataProtectionProvider protection) {
 readonly IDataProtector protector=protection.CreateProtector("SEM.BusinessNotifications.v1");
 public BusinessEmailPayload Read(BusinessNotification item)=>JsonSerializer.Deserialize<BusinessEmailPayload>(protector.Unprotect(item.ProtectedPayload))!;
 public void Enqueue(ApplicationDbContext db,string operationId,SessionEvent e,ApplicationUser actor,string subjectId,
 string kind,string reason,BusinessSlot[] slots,DateTimeOffset now){
  if(kind is not ("Register" or "Release")||slots.Length==0)return;
  var action=kind=="Register"?"登録":"登録解除";
  var at=now.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss zzz");
  var when=DateTimeOffset.Parse(e.ScheduledDate,System.Globalization.CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm zzz");
  var title=$"演奏パート{action}のお知らせ";
  var body=$"イベント：{e.Title}\n開催日時：{when}（日本時間）\n操作：{action}\n確定日時：{at}（日本時間）\n操作者：{actor.DisplayName}\n"+
   string.Join("\n",slots.Select(s=>$"曲：{s.SongTitle} ／ パート：{s.PartName}"))+(kind=="Release"?$"\n解除理由：{reason}":"")+"\n\nこれは確定済みの操作のお知らせです。アプリ内の最新状態をご確認ください。";
  var payload=new BusinessEmailPayload(e.Title,e.ScheduledDate,actor.DisplayName,kind=="Release"?reason:"",slots,title,body);
  db.BusinessNotifications.Add(new(){Id=operationId,EventId=e.Id,UserId=subjectId,Kind=kind,
   CreatedAt=now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),ProtectedPayload=protector.Protect(JsonSerializer.Serialize(payload)),Due=now.ToUnixTimeSeconds()});
 }
}
public interface IBusinessEmailTransport {
 Task Send(string id,ApplicationUser user,BusinessEmailPayload payload,CancellationToken ct);
}
public sealed class BusinessEmailTransport(NotificationService notifications):IBusinessEmailTransport {
 public Task Send(string id,ApplicationUser user,BusinessEmailPayload payload,CancellationToken ct)=>notifications.SendBusinessEmail(id,user,payload.Subject,payload.Body,ct);
}
public sealed class BusinessEmailDispatcher(IDbContextFactory<ApplicationDbContext> factory,BusinessNotificationStore store,
 IBusinessEmailTransport transport,TimeProvider clock,ILogger<BusinessEmailDispatcher> logger) {
 // Lease exceeds the existing 15-second SMTP timeout. It is reclaimed after crash/restart.
 const int LeaseSeconds=120,MaxAttempts=5;
 public async Task DispatchOnce(CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);
  var now=clock.GetUtcNow().ToUnixTimeSeconds();
  // Last-attempt abandoned leases are terminal, not silently stuck forever.
  await db.BusinessNotifications.Where(x=>x.Status=="Sending"&&x.Attempts>=MaxAttempts&&x.LeaseUntil<=now)
   .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Failed").SetProperty(x=>x.Error,"LeaseExpired").SetProperty(x=>x.Claim,(string?)null).SetProperty(x=>x.LeaseUntil,(long?)null),ct);
  var ids=await db.BusinessNotifications.AsNoTracking().Where(x=>x.Attempts<MaxAttempts&&x.Due<=now&&(x.Status=="Pending"||x.Status=="Sending"&&x.LeaseUntil<=now))
   .OrderBy(x=>x.Due).ThenBy(x=>x.Id).Select(x=>x.Id).Take(20).ToListAsync(ct);
  foreach(var id in ids){
   ct.ThrowIfCancellationRequested();now=clock.GetUtcNow().ToUnixTimeSeconds();var claim=Guid.NewGuid().ToString();
   var claimed=await db.BusinessNotifications.Where(x=>x.Id==id&&x.Attempts<MaxAttempts&&x.Due<=now&&(x.Status=="Pending"||x.Status=="Sending"&&x.LeaseUntil<=now))
    .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,"Sending").SetProperty(x=>x.Claim,claim).SetProperty(x=>x.LeaseUntil,now+LeaseSeconds).SetProperty(x=>x.Attempts,x=>x.Attempts+1),ct);
   if(claimed!=1)continue;
   var item=await db.BusinessNotifications.AsNoTracking().SingleAsync(x=>x.Id==id&&x.Claim==claim,ct);
   try{
    var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==item.UserId,ct);
    if(user is null||!user.Enabled||!user.EmailConfirmed||string.IsNullOrWhiteSpace(user.Email)){
     await Complete(db,id,claim,"Cancelled","RecipientUnavailable",false,ct);continue;
    }
    // Do not expose ciphertext, email addresses, or SMTP error text in diagnostics.
    await transport.Send(item.Id,user,store.Read(item),ct);
    await Complete(db,id,claim,"Sent",null,true,ct);
   }catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
   catch(Exception ex){
    var retryAt=clock.GetUtcNow().ToUnixTimeSeconds()+(long)Math.Pow(2,item.Attempts)*15;
    await db.BusinessNotifications.Where(x=>x.Id==id&&x.Claim==claim&&x.Status=="Sending")
     .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,item.Attempts>=MaxAttempts?"Failed":"Pending").SetProperty(x=>x.Due,retryAt)
      .SetProperty(x=>x.Error,ex.GetType().Name).SetProperty(x=>x.Claim,(string?)null).SetProperty(x=>x.LeaseUntil,(long?)null),ct);
    logger.LogWarning("Business email attempt failed; attempt={Attempt}, type={Type}",item.Attempts,ex.GetType().Name);
   }
  }
 }
 async Task Complete(ApplicationDbContext db,string id,string claim,string status,string? error,bool sent,CancellationToken ct){
  long? at=sent?clock.GetUtcNow().ToUnixTimeSeconds():null;
  await db.BusinessNotifications.Where(x=>x.Id==id&&x.Claim==claim&&x.Status=="Sending")
   .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Status,status).SetProperty(x=>x.Error,error).SetProperty(x=>x.SentAt,at)
    .SetProperty(x=>x.Claim,(string?)null).SetProperty(x=>x.LeaseUntil,(long?)null),ct);
 }
}
public sealed class BusinessEmailWorker(IServiceScopeFactory scopes,ILogger<BusinessEmailWorker> logger):BackgroundService {
 protected override async Task ExecuteAsync(CancellationToken ct){
  using var timer=new PeriodicTimer(TimeSpan.FromSeconds(3));
  while(await timer.WaitForNextTickAsync(ct)){
   try{await using var scope=scopes.CreateAsyncScope();await scope.ServiceProvider.GetRequiredService<BusinessEmailDispatcher>().DispatchOnce(ct);}
   catch(OperationCanceledException)when(ct.IsCancellationRequested){return;}
   catch(Exception ex){logger.LogWarning("Business email worker iteration failed; type={Type}",ex.GetType().Name);}
  }
 }
}
