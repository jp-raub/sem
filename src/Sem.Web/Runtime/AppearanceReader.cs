using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Authentication;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed record AppearanceColors(string Accent,string Background,string Surface,string Text);
public sealed record AppearanceView(string ActorId,string? EventId,string Palette,string Mode,AppearanceColors? Colors);
public sealed record AppearanceResult(string State,AppearanceView? View=null);
public static class AppearanceReader {
 static readonly Dictionary<string,string> NativeAccents=new(StringComparer.Ordinal){["green"]="#1E6557",["blue"]="#285F91",["purple"]="#775087",["amber"]="#906322"};
 static bool Color(string? value)=>value is not null&&value.Length==7&&Regex.IsMatch(value,"^#[0-9a-fA-F]{6}$",RegexOptions.CultureInvariant);
 public static async Task<AppearanceResult> ReadAsync(ApplicationDbContext db,ClaimsPrincipal principal,IdentityOptions identity,string? eventId,DateTimeOffset now,CancellationToken ct=default){
  await db.Database.OpenConnectionAsync(ct);
  try{
   using var snapshot=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);await using var tx=await db.Database.UseTransactionAsync(snapshot,ct);
   if(!await AuthenticationSession.ValidAsync(db,principal,identity,ct))return new("unauthorized");
   var userId=AuthenticationSession.SingleClaim(principal,identity.ClaimsIdentity.UserIdClaimType)!;
   if(eventId is not null){var actorAdmin=await db.Users.AsNoTracking().Where(u=>u.Id==userId).Select(u=>u.IsAdmin).SingleAsync(ct);var e=await db.SessionEvents.AsNoTracking().Include(e=>e.Admins).Include(e=>e.Phases).SingleOrDefaultAsync(e=>e.Id==eventId,ct);var phase=e is null?null:EventListReader.Effective(e,now);if(e is null||phase is null||EventListReader.Instant(e.ScheduledDate) is null||phase.Kind=="Hidden"&&!(actorAdmin||e.Admins.Any(a=>a.UserId==userId)))return new("not-found");}
   var personal=await db.UserThemePreferences.AsNoTracking().Where(p=>p.UserId==userId).Select(p=>new{p.ThemeId,p.Mode}).SingleOrDefaultAsync(ct);
   var eventLayer=eventId is null?null:await db.EventThemePreferences.AsNoTracking().Where(p=>p.EventId==eventId).Select(p=>new{p.ThemeId,p.Mode}).SingleOrDefaultAsync(ct);
   var site=await db.SiteThemePreferences.AsNoTracking().Where(p=>p.Id==1).Select(p=>new{p.ThemeId,p.Mode}).SingleOrDefaultAsync(ct);
   var layers=new[]{personal,eventLayer,site};if(layers.Any(l=>l?.Mode is not null&&l.Mode is not(0 or 1 or 2)))return new("invalid");
   var selected=layers.Select(l=>l?.ThemeId).FirstOrDefault(x=>x is not null)??"green";
   var ids=layers.Where(l=>l?.ThemeId is not null).Select(l=>l!.ThemeId!).Append(selected).Distinct().ToArray();
   var entries=await db.ThemeCatalogEntries.AsNoTracking().Where(t=>ids.Contains(t.Id)).Select(t=>new{t.Id,t.Accent,t.Background,t.Surface,t.Text,t.BuiltIn}).ToArrayAsync(ct);
   // 影に隠れた下位参照も検証する。欠損参照を無言で別テーマへ読み替えない。
   if(ids.Any(id=>entries.All(t=>t.Id!=id))||entries.Any(t=>!Color(t.Accent)||!Color(t.Background)||!Color(t.Surface)||!Color(t.Text)))return new("invalid");
   var theme=entries.Single(t=>t.Id==selected);var colors=new AppearanceColors(theme.Accent.ToUpperInvariant(),theme.Background.ToUpperInvariant(),theme.Surface.ToUpperInvariant(),theme.Text.ToUpperInvariant());
   var native=theme.BuiltIn&&NativeAccents.TryGetValue(theme.Id,out var accent)&&colors==new AppearanceColors(accent,"#F5F7F3","#FFFEFA","#18312E");
   var mode=layers.Select(l=>l?.Mode).FirstOrDefault(m=>m.HasValue)??0;
   var view=new AppearanceView(userId,eventId,NativeAccents.ContainsKey(theme.Id)?theme.Id:"green",mode==0?"system":mode==1?"light":"dark",native?null:colors);
   await snapshot.CommitAsync(ct);return new("ok",view);
  }finally{await db.Database.CloseConnectionAsync();}
 }
}
public static class AppearanceEndpoints {
 public static void MapSemAppearance(this WebApplication app){
  app.MapGet("/api/ui/appearance",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t)=>Read(c,db,i,t,null)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/events/{id}/appearance",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t,string id)=>Read(c,db,i,t,id)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
 }
 static async Task<IResult> Read(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider clock,string? id){
  if(id is not null&&id.Length is <1 or >128)return Results.BadRequest();
  if(c.Request.QueryString.HasValue||c.Request.ContentLength is >0||c.Request.Headers.ContainsKey("Transfer-Encoding"))return Results.BadRequest();
  var r=await AppearanceReader.ReadAsync(db,c.User,i.Value,id,clock.GetUtcNow(),c.RequestAborted);
  return r.State switch{"unauthorized"=>Results.Unauthorized(),"not-found"=>Results.NotFound(),"invalid"=>Results.Problem(statusCode:422,title:"保存済み配色を表示できません。管理者へ確認してください。"),_=>Results.Json(r.View)};
 }
}
