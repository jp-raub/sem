using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public static class ProfileEndpoints {
 public static void MapSemProfiles(this WebApplication app){
  async Task<IResult> Read(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> identity,TimeProvider clock,string id,bool image){
   if(c.Request.QueryString.HasValue||id.Length is <1 or >128)return Results.BadRequest();
   var r=await ProfileReader.ReadAsync(db,c.User,identity.Value,id,clock.GetUtcNow(),image,c.RequestAborted);
   return r.State switch{"unauthorized"=>Results.Unauthorized(),"not-found"=>Results.NotFound(),"invalid"=>Results.Problem(statusCode:422,title:"保存済みプロフィールを表示できません。管理者へ確認してください。"),_=>image?Results.File(r.Image!,"image/png"):Results.Json(r.View)};
  }
  app.MapGet("/api/ui/profiles/{id}",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t,string id)=>Read(c,db,i,t,id,false)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/profiles/{id}/image",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t,string id)=>Read(c,db,i,t,id,true)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
 }
}
