using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Authentication;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed record InvitationRequestView(string Id,string Reason,int Count,int Days,string Status,string? CreatedAt,string? DecidedAt,string? DecisionComment,string? DeciderName);
public sealed record InvitationRecordView(string Id,int? Limit,int Uses,string? CreatedAt,string? ExpiresAt,string State);
public sealed record InvitationBox(string ActorId,string DisplayName,string SiteName,bool IsAdmin,int Page,bool MoreRequests,bool MoreLinks,InvitationRequestView[] Requests,InvitationRecordView[] Links);
public sealed record InvitationReadResult(string State,InvitationBox? View=null);
public static class InvitationReader {
 static string? Stamp(long? n)=>n is null or 0?null:DateTimeOffset.FromUnixTimeSeconds(n.Value).ToString("O");
 public static async Task<InvitationReadResult> ReadAsync(ApplicationDbContext db,ClaimsPrincipal principal,IdentityOptions identity,int page,CancellationToken ct){
  if(page<0||page>int.MaxValue/100)return new("invalid-request");await db.Database.OpenConnectionAsync(ct);
  try{using var snapshot=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);await using var tx=await db.Database.UseTransactionAsync(snapshot,ct);
   if(!await AuthenticationSession.ValidAsync(db,principal,identity,ct))return new("unauthorized");
   var id=AuthenticationSession.SingleClaim(principal,identity.ClaimsIdentity.UserIdClaimType)!;
   var actor=await db.Users.AsNoTracking().Where(u=>u.Id==id).Select(u=>new{u.DisplayName,u.IsAdmin}).SingleAsync(ct);
   // SQLの段階で本人へ限定。全体管理者の判断・台帳APIではない。秘密列は読み出さない。
   var requests=await db.InvitationRequests.AsNoTracking().Where(r=>r.UserId==id).OrderByDescending(r=>r.Created).ThenByDescending(r=>r.Id).Skip(checked(page*100)).Take(101).Select(r=>new{r.Id,r.Reason,r.Count,r.Days,r.Status,r.Created,r.DecidedAt,r.DecisionComment,r.DecidedBy}).ToArrayAsync(ct);
   var links=await db.Invitations.AsNoTracking().Where(r=>r.OwnerId==id).OrderByDescending(r=>r.Created).ThenByDescending(r=>r.Id).Skip(checked(page*100)).Take(101).Select(r=>new{r.Id,r.Limit,r.Uses,r.Created,r.Expires,r.Disabled}).ToArrayAsync(ct);
   if(actor.DisplayName.Length>2000||requests.Any(r=>r.Id.Length>128||r.Reason.Length>500||r.Count is <1 or >5||r.Days is <1 or >7||(r.DecisionComment?.Length??0)>500)||links.Any(r=>r.Id.Length>128||r.Uses<0||r.Limit<0))return new("invalid");
   var deciders=requests.Take(100).Select(r=>r.DecidedBy).Where(s=>s!=null).ToArray();var names=await db.Users.AsNoTracking().Where(u=>deciders.Contains(u.Id)).Select(u=>new{u.Id,u.DisplayName}).ToDictionaryAsync(u=>u.Id,u=>u.DisplayName,ct);
   if(names.Values.Any(n=>n.Length>2000))return new("invalid");
   var site=await db.ServiceSettings.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??await db.SiteStates.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??"SESSION ROOM";
   var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
   var view=new InvitationBox(id,actor.DisplayName,site,actor.IsAdmin,page,requests.Length>100,links.Length>100,
    requests.Take(100).Select(r=>new InvitationRequestView(r.Id,r.Reason,r.Count,r.Days,r.Status is "Pending" or "Approved" or "Rejected" or "LegacyApproved"?r.Status:"Unknown",Stamp(r.Created),Stamp(r.DecidedAt),r.DecisionComment,r.DecidedBy!=null&&names.TryGetValue(r.DecidedBy,out var n)?n:null)).ToArray(),
    links.Take(100).Select(r=>new InvitationRecordView(r.Id,r.Limit==0?null:r.Limit,r.Uses,Stamp(r.Created),Stamp(r.Expires),r.Disabled?"Disabled":r.Expires!=0&&r.Expires<=now?"Expired":r.Limit>0&&r.Uses>=r.Limit?"Full":r.Expires==0||r.Limit==0?"Unknown":"Active")).ToArray());
   await snapshot.CommitAsync(ct);return new("ok",view);
  }catch(ArgumentOutOfRangeException){return new("invalid");}finally{await db.Database.CloseConnectionAsync();}
 }
}
public static class InvitationEndpoints {
 public static void MapSemInvitations(this WebApplication app){
  app.MapGet("/api/ui/invitations",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i)=>Read(c,db,i,0)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/invitations/page/{page:int}",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,int page)=>Read(c,db,i,page)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
 }
 static async Task<IResult> Read(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,int page){
  if(c.Request.QueryString.HasValue||c.Request.ContentLength is >0||c.Request.Headers.ContainsKey("Transfer-Encoding"))return Results.BadRequest();
  var r=await InvitationReader.ReadAsync(db,c.User,i.Value,page,c.RequestAborted);return r.State switch{"unauthorized"=>Results.Unauthorized(),"invalid-request"=>Results.BadRequest(),"invalid"=>Results.Problem(statusCode:422,title:"保存済み招待情報を表示できません。管理者へ確認してください。"),_=>Results.Json(r.View)};
 }
}
