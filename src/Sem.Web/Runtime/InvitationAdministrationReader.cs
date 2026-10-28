using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Authentication;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed record AdministrativeRequest(InvitationRequestView Record,string? RequesterName);
public sealed record AdministrativeInvitation(InvitationRecordView Record,string Source,string? RequesterName,string? IssuerName,string? Purpose,string? RevokerName,string? RevocationReason,string? RevokedAt);
public sealed record InvitationAdministration(string ActorId,string SiteName,int Page,bool MoreRequests,bool MoreLinks,AdministrativeRequest[] Requests,AdministrativeInvitation[] Links);
public sealed record InvitationAdministrationResult(string State,InvitationAdministration? View=null);
public static class InvitationAdministrationReader {
 static string? Stamp(long? n)=>n is null or 0?null:DateTimeOffset.FromUnixTimeSeconds(n.Value).ToString("O");
 public static async Task<InvitationAdministrationResult> ReadAsync(ApplicationDbContext db,ClaimsPrincipal principal,IdentityOptions identity,int page,CancellationToken ct=default){
  if(page<0||page>int.MaxValue/100)return new("invalid-request");await db.Database.OpenConnectionAsync(ct);
  try{using var snapshot=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);await using var tx=await db.Database.UseTransactionAsync(snapshot,ct);
   if(!await AuthenticationSession.ValidAsync(db,principal,identity,ct))return new("unauthorized");
   var id=AuthenticationSession.SingleClaim(principal,identity.ClaimsIdentity.UserIdClaimType)!;
   if(!await db.Users.AsNoTracking().AnyAsync(u=>u.Id==id&&u.IsAdmin,ct))return new("forbidden");
   // 秘密トークン・保護列・メールを取得せず、現在DBの全体管理者だけに必要な記録を射影する。
   var requests=await db.InvitationRequests.AsNoTracking().OrderByDescending(r=>r.Created).ThenByDescending(r=>r.Id).Skip(checked(page*100)).Take(101).Select(r=>new{r.Id,r.UserId,r.Reason,r.Count,r.Days,r.Status,r.Created,r.DecidedAt,r.DecisionComment,r.DecidedBy}).ToArrayAsync(ct);
   var links=await db.Invitations.AsNoTracking().OrderByDescending(r=>r.Created).ThenByDescending(r=>r.Id).Skip(checked(page*100)).Take(101).Select(r=>new{r.Id,r.OwnerId,r.IssuerId,r.RequestId,r.IssuanceSource,r.Purpose,r.Limit,r.Uses,r.Created,r.Expires,r.Disabled,r.RevokedBy,r.RevocationReason,r.RevokedAt}).ToArrayAsync(ct);
   if(requests.Any(r=>r.Id.Length is 0 or >128||r.Reason.Length>500||r.Count is <1 or >5||r.Days is <1 or >7||(r.DecisionComment?.Length??0)>500)||links.Any(r=>r.Id.Length is 0 or >128||r.Uses<0||r.Limit<0||(r.Purpose?.Length??0)>500||(r.RevocationReason?.Length??0)>500))return new("invalid");
   var requestIds=links.Take(100).Select(r=>r.RequestId).Where(x=>x!=null).ToArray();
   var knownRequests=await db.InvitationRequests.AsNoTracking().Where(r=>requestIds.Contains(r.Id)).Select(r=>new{r.Id,r.UserId}).ToDictionaryAsync(r=>r.Id,r=>r.UserId,ct);
   var ids=requests.Take(100).SelectMany(r=>new[]{r.UserId,r.DecidedBy}).Concat(links.Take(100).SelectMany(r=>new[]{r.OwnerId,r.IssuerId,r.RevokedBy})).Concat(knownRequests.Values).Where(s=>!string.IsNullOrEmpty(s)).Distinct().ToArray();
   var names=await db.Users.AsNoTracking().Where(u=>ids.Contains(u.Id)).Select(u=>new{u.Id,u.DisplayName}).ToDictionaryAsync(u=>u.Id,u=>u.DisplayName,ct);if(names.Values.Any(n=>n.Length>2000))return new("invalid");
   string? Name(string? key)=>key!=null&&names.TryGetValue(key,out var n)?n:null;
   var site=await db.ServiceSettings.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??await db.SiteStates.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??"SESSION ROOM";
   var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
   var view=new InvitationAdministration(id,site,page,requests.Length>100,links.Length>100,
    requests.Take(100).Select(r=>new AdministrativeRequest(new(r.Id,r.Reason,r.Count,r.Days,r.Status is "Pending" or "Approved" or "Rejected" or "LegacyApproved"?r.Status:"Unknown",Stamp(r.Created),Stamp(r.DecidedAt),r.DecisionComment,Name(r.DecidedBy)),Name(r.UserId))).ToArray(),
    links.Take(100).Select(r=>new AdministrativeInvitation(new(r.Id,r.Limit==0?null:r.Limit,r.Uses,Stamp(r.Created),Stamp(r.Expires),r.Disabled?"Disabled":r.Expires!=0&&r.Expires<=now?"Expired":r.Limit>0&&r.Uses>=r.Limit?"Full":r.Expires==0||r.Limit==0?"Unknown":"Active"),r.IssuanceSource=="Direct"?"Direct":(r.IssuanceSource=="Request"||r.RequestId!=null&&knownRequests.ContainsKey(r.RequestId))?"Request":"Unknown",r.RequestId!=null&&knownRequests.TryGetValue(r.RequestId,out var requester)?Name(requester):null,Name(r.IssuerId),r.Purpose,Name(r.RevokedBy),r.RevocationReason,Stamp(r.RevokedAt))).ToArray());
   await snapshot.CommitAsync(ct);return new("ok",view);
  }catch(ArgumentOutOfRangeException){return new("invalid");}finally{await db.Database.CloseConnectionAsync();}
 }
 public static void MapSemInvitationAdministration(this WebApplication app){
  app.MapGet("/api/ui/administration/invitations",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i)=>Read(c,db,i,0)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/administration/invitations/page/{page:int}",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,int page)=>Read(c,db,i,page)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
 }
 static async Task<IResult> Read(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,int page){
  if(c.Request.QueryString.HasValue||c.Request.ContentLength is >0||c.Request.Headers.ContainsKey("Transfer-Encoding"))return Results.BadRequest();
  var r=await ReadAsync(db,c.User,i.Value,page,c.RequestAborted);return r.State switch{"unauthorized"=>Results.Unauthorized(),"forbidden"=>Results.StatusCode(403),"invalid-request"=>Results.BadRequest(),"invalid"=>Results.Problem(statusCode:422,title:"保存済み招待情報を表示できません。"),_=>Results.Json(r.View)};
 }
}
