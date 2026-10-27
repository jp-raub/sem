using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Authentication;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed record MessageItem(string Id,string Title,string? CreatedAt,bool IsRead);
public sealed record MessageBox(string ActorId,string SiteName,int Page,bool HasMore,MessageItem[] Messages);
public sealed record UnreadMessages(string ActorId,int Count);
public sealed record MessageBody(string ActorId,string Id,string Title,string Body,string? CreatedAt,bool IsRead);
public sealed record MessageResult(string State,object? View=null);
public static class MessageReader {
 static string? Stamp(long n)=>n==0?null:DateTimeOffset.FromUnixTimeSeconds(n).ToString("O");
 public static async Task<MessageResult> ReadAsync(ApplicationDbContext db,ClaimsPrincipal principal,IdentityOptions identity,int page=0,string? id=null,bool unread=false,CancellationToken ct=default){
  if(page<0||page>int.MaxValue/100)return new("invalid-request");await db.Database.OpenConnectionAsync(ct);
  try{using var snapshot=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);await using var tx=await db.Database.UseTransactionAsync(snapshot,ct);
   if(!await AuthenticationSession.ValidAsync(db,principal,identity,ct))return new("unauthorized");
   var actor=AuthenticationSession.SingleClaim(principal,identity.ClaimsIdentity.UserIdClaimType)!;var mine=db.Messages.AsNoTracking().Where(m=>m.UserId==actor);object view;
   if(unread)view=new UnreadMessages(actor,await mine.CountAsync(m=>m.ReadAt==null,ct));
   else if(id is not null){var m=await mine.Where(m=>m.Id==id).Select(m=>new{m.Id,m.Title,m.Body,m.Created,IsRead=m.ReadAt!=null}).SingleOrDefaultAsync(ct);if(m is null)return new("not-found");if(m.Title.Length>2000||m.Body.Length>65536)return new("invalid");view=new MessageBody(actor,m.Id,m.Title,m.Body,Stamp(m.Created),m.IsRead);}
   else{var rows=await mine.OrderByDescending(m=>m.Created).ThenByDescending(m=>m.Id).Skip(checked(page*100)).Take(101).Select(m=>new{m.Id,m.Title,m.Created,IsRead=m.ReadAt!=null}).ToArrayAsync(ct);if(rows.Any(m=>m.Id.Length>128||m.Title.Length>2000))return new("invalid");var site=await db.ServiceSettings.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??await db.SiteStates.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??"SESSION ROOM";view=new MessageBox(actor,site,page,rows.Length>100,rows.Take(100).Select(m=>new MessageItem(m.Id,m.Title,Stamp(m.Created),m.IsRead)).ToArray());}
   await snapshot.CommitAsync(ct);return new("ok",view);
  }catch(ArgumentOutOfRangeException){return new("invalid");}finally{await db.Database.CloseConnectionAsync();}
 }
}
public static class MessageEndpoints {
 public static void MapSemMessages(this WebApplication app){
  app.MapGet("/api/ui/messages",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i)=>Read(c,db,i)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/messages/unread",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i)=>Read(c,db,i,unread:true)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/messages/page/{page:int}",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,int page)=>Read(c,db,i,page)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/messages/{id}",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,string id)=>Read(c,db,i,id:id)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
 }
 static async Task<IResult> Read(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,int page=0,string? id=null,bool unread=false){
  if(c.Request.QueryString.HasValue||c.Request.ContentLength is >0||c.Request.Headers.ContainsKey("Transfer-Encoding")||id is not null&&id.Length is <1 or >128)return Results.BadRequest();
  var r=await MessageReader.ReadAsync(db,c.User,i.Value,page,id,unread,c.RequestAborted);return r.State switch{"unauthorized"=>Results.Unauthorized(),"not-found"=>Results.NotFound(),"invalid-request"=>Results.BadRequest(),"invalid"=>Results.Problem(statusCode:422,title:"保存済みメッセージを表示できません。管理者へ確認してください。"),_=>Results.Json(r.View)};
 }
}
