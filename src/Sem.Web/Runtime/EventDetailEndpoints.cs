using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Sem.Authentication;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public static class EventDetailEndpoints {
 public const string ReadPolicy="sem-event-read";
 public static void AddSemEventReads(this IServiceCollection services)=>services.AddRateLimiter(o=>o.AddPolicy(ReadPolicy,c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=180,Window=TimeSpan.FromMinutes(1),QueueLimit=0})));
 public static void MapSemEventDetail(this WebApplication app){
  async Task<IResult> Read(HttpContext context,ApplicationDbContext db,IOptions<IdentityOptions> identity,TimeProvider clock,string id,bool image){
   if(context.Request.QueryString.HasValue||id.Length is <1 or >128)return Results.BadRequest();
   var result=await EventDetailReader.ReadAsync(db,context.User,identity.Value,id,clock.GetUtcNow(),image,context.RequestAborted);
   return result.State switch{"unauthorized"=>Results.Unauthorized(),"not-found"=>Results.NotFound(),"invalid"=>Results.Problem(statusCode:422,title:"保存済みデータを表示できません。管理者へ確認してください。"),_=>image?Results.File(result.Image!,"image/png"):Results.Json(result.View)};
  }
  app.MapGet("/api/ui/events/{id}",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t,string id)=>Read(c,db,i,t,id,false)).RequireRateLimiting(ReadPolicy);
  app.MapGet("/api/ui/events/{id}/header",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t,string id)=>Read(c,db,i,t,id,true)).RequireRateLimiting(ReadPolicy);
 }
}
