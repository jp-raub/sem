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
public sealed record AppearanceChoice(string Id,string Name,string Palette,AppearanceColors? Colors);
public sealed record AppearanceEditor(string ActorId,string? EventId,string? Color,string? Mode,AppearanceChoice[] Choices,AppearanceView Current,AppearanceView Inherited);
public sealed record AppearanceResult(string State,AppearanceView? View=null,AppearanceEditor? Editor=null);
public static class AppearanceReader {
 static readonly Dictionary<string,string> NativeAccents=new(StringComparer.Ordinal){["green"]="#1E6557",["blue"]="#285F91",["purple"]="#775087",["amber"]="#906322"};
 static bool Color(string? value)=>value is not null&&value.Length==7&&Regex.IsMatch(value,"^#[0-9a-fA-F]{6}$",RegexOptions.CultureInvariant);
 public static async Task<AppearanceResult> ReadAsync(ApplicationDbContext db,ClaimsPrincipal principal,IdentityOptions identity,string? eventId,DateTimeOffset now,CancellationToken ct=default,bool editor=false){
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
   var inheritedId=new[]{eventLayer,site}.Select(l=>l?.ThemeId).FirstOrDefault(x=>x is not null)??"green";
   var ids=layers.Where(l=>l?.ThemeId is not null).Select(l=>l!.ThemeId!).Append(selected).Append(inheritedId).Distinct().ToArray();
   var query=db.ThemeCatalogEntries.AsNoTracking();
   // 編集候補の公開は認可後だけ。選択結果の通常参照ではカタログ全体を返さない。
   var entries=await (editor?query:query.Where(t=>ids.Contains(t.Id))).OrderBy(t=>t.Id).Select(t=>new{t.Id,t.Name,t.Accent,t.Background,t.Surface,t.Text,t.BuiltIn}).Take(1025).ToArrayAsync(ct);
   if(entries.Length>1024||ids.Any(id=>entries.All(t=>t.Id!=id))||entries.Any(t=>!Color(t.Accent)||!Color(t.Background)||!Color(t.Surface)||!Color(t.Text)||editor&&(string.IsNullOrWhiteSpace(t.Id)||t.Id.Length>128||string.IsNullOrWhiteSpace(t.Name)||t.Name.Length>256)))return new("invalid");
   AppearanceChoice Choice(string id){var t=entries.Single(t=>t.Id==id);var colors=new AppearanceColors(t.Accent.ToUpperInvariant(),t.Background.ToUpperInvariant(),t.Surface.ToUpperInvariant(),t.Text.ToUpperInvariant());var native=t.BuiltIn&&NativeAccents.TryGetValue(t.Id,out var accent)&&colors==new AppearanceColors(accent,"#F5F7F3","#FFFEFA","#18312E");return new(t.Id,t.Name,NativeAccents.ContainsKey(t.Id)?t.Id:"green",native?null:colors);}
   AppearanceView View(string id,int mode){var c=Choice(id);return new(userId,eventId,c.Palette,mode==0?"system":mode==1?"light":"dark",c.Colors);}
   var view=View(selected,layers.Select(l=>l?.Mode).FirstOrDefault(m=>m.HasValue)??0);
   var inherited=View(inheritedId,new[]{eventLayer,site}.Select(l=>l?.Mode).FirstOrDefault(m=>m.HasValue)??0);
   var settings=editor?new AppearanceEditor(userId,eventId,personal?.ThemeId,personal?.Mode is null?null:personal.Mode==0?"system":personal.Mode==1?"light":"dark",entries.OrderBy(t=>Array.IndexOf(new[]{"green","blue","purple","amber","rose","teal","slate","navy","coral","mono"},t.Id) is var rank&&rank>=0?rank:10).ThenBy(t=>t.Id,StringComparer.Ordinal).Select(t=>Choice(t.Id)).ToArray(),view,inherited):null;
   await snapshot.CommitAsync(ct);return new("ok",view,settings);
  }finally{await db.Database.CloseConnectionAsync();}
 }
}
public static class AppearanceEndpoints {
 public static void MapSemAppearance(this WebApplication app){
  app.MapGet("/api/ui/appearance/editor",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t)=>Read(c,db,i,t,null,true)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/events/{id}/appearance/editor",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t,string id)=>Read(c,db,i,t,id,true)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/appearance",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t)=>Read(c,db,i,t,null)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/events/{id}/appearance",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t,string id)=>Read(c,db,i,t,id)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
 }
 static async Task<IResult> Read(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider clock,string? id,bool editor=false){
  if(id is not null&&id.Length is <1 or >128)return Results.BadRequest();
  if(c.Request.QueryString.HasValue||c.Request.ContentLength is >0||c.Request.Headers.ContainsKey("Transfer-Encoding"))return Results.BadRequest();
  var r=await AppearanceReader.ReadAsync(db,c.User,i.Value,id,clock.GetUtcNow(),c.RequestAborted,editor);
  return r.State switch{"unauthorized"=>Results.Unauthorized(),"not-found"=>Results.NotFound(),"invalid"=>Results.Problem(statusCode:422,title:"保存済み配色を表示できません。管理者へ確認してください。"),_=>editor?Results.Json(r.Editor):Results.Json(r.View)};
 }
}
