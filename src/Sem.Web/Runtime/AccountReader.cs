using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sem.Authentication;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed record AccountKeyView(string Name,string? CreatedAt);
public sealed record AccountView(string ActorId,string SiteName,string Email,AccountKeyView[] Passkeys);
public sealed record AccountReadResult(string State,AccountView? View=null);
public static class AccountReader {
 public static async Task<AccountReadResult> ReadAsync(ApplicationDbContext db,UserManager<ApplicationUser> users,ClaimsPrincipal principal,IdentityOptions identity,CancellationToken ct=default){
  await db.Database.OpenConnectionAsync(ct);
  try{
   using var snapshot=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);await using var tx=await db.Database.UseTransactionAsync(snapshot,ct);
   if(!await AuthenticationSession.ValidAsync(db,principal,identity,ct))return new("unauthorized");
   var id=AuthenticationSession.SingleClaim(principal,identity.ClaimsIdentity.UserIdClaimType)!;
   var actor=await db.Users.AsNoTracking().SingleAsync(u=>u.Id==id,ct);
   if(string.IsNullOrWhiteSpace(actor.Email)||actor.Email.Length>254)return new("invalid");
   // 標準ストアで本人のキーを読む。Credential ID・公開鍵・署名回数等はDTOにしない。
   var keys=await users.GetPasskeysAsync(actor);
   if(keys.Count>512||keys.Any(k=>(k.Name?.Length??0)>200))return new("invalid");
   var site=await db.ServiceSettings.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??await db.SiteStates.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??"SESSION ROOM";
   var view=new AccountView(id,site,actor.Email,keys.Select(k=>new AccountKeyView(k.Name??"",k.CreatedAt==default?null:k.CreatedAt.ToUniversalTime().ToString("O"))).ToArray());
   await snapshot.CommitAsync(ct);return new("ok",view);
  }finally{await db.Database.CloseConnectionAsync();}
 }
}
public static class AccountEndpoints {
 public static void MapSemAccount(this WebApplication app)=>app.MapGet("/api/ui/account",async(HttpContext c,ApplicationDbContext db,UserManager<ApplicationUser> users,Microsoft.Extensions.Options.IOptions<IdentityOptions> identity)=>{
  if(c.Request.QueryString.HasValue||c.Request.ContentLength is >0||c.Request.Headers.ContainsKey("Transfer-Encoding"))return Results.BadRequest();
  var r=await AccountReader.ReadAsync(db,users,c.User,identity.Value,c.RequestAborted);
  return r.State switch{"unauthorized"=>Results.Unauthorized(),"invalid"=>Results.Problem(statusCode:422,title:"保存済みアカウントを表示できません。管理者へ確認してください。"),_=>Results.Json(r.View)};
 }).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
}
