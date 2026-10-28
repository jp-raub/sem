using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sem.Authentication;
using Sem.Web.Data;
using Sem.Web.Core;
namespace Sem.Web.Runtime;
public sealed record OverviewTheme(string Id,string Name);
public sealed record EventOverviewView(string ActorId,string EventId,string Title,string ScheduledDate,string? OpensAt,string Location,string LocationUrl,string Icon,int? Capacity,int ParticipantCount,string Description,string DescriptionFormat,string DescriptionHtml,string AnnouncementMessage,bool HasHeader,string? Color,string? Mode,OverviewTheme[] Choices);
public sealed record EventOverviewResult(string State,EventOverviewView? View=null);
public static class EventOverviewReader {
 public static async Task<EventOverviewResult> ReadAsync(ApplicationDbContext db,ClaimsPrincipal principal,IdentityOptions identity,string id,DateTimeOffset now,CancellationToken ct=default){
  await db.Database.OpenConnectionAsync(ct);
  try{using var snapshot=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);await using var tx=await db.Database.UseTransactionAsync(snapshot,ct);
   if(!await AuthenticationSession.ValidAsync(db,principal,identity,ct))return new("unauthorized");
   var actor=AuthenticationSession.SingleClaim(principal,identity.ClaimsIdentity.UserIdClaimType)!;
   var admin=await db.Users.AsNoTracking().Where(u=>u.Id==actor).Select(u=>u.IsAdmin).SingleAsync(ct);
   var e=await db.SessionEvents.AsNoTracking().Where(e=>e.Id==id).Include(e=>e.Phases).Include(e=>e.Admins).SingleOrDefaultAsync(ct);
   // 公開イベントでも原文を一般閲覧者へ渡さない。担当権限は現DBだけを正とする。
   if(e is null||!admin&&!e.Admins.Any(a=>a.UserId==actor)||EventListReader.Effective(e,now) is null)return new("not-found");
   if(EventListReader.Instant(e.ScheduledDate) is null||e.OpensAt is not null&&(EventListReader.Instant(e.OpensAt) is not{} opening||opening>EventListReader.Instant(e.ScheduledDate))||e.Capacity is <0||e.DescriptionFormat is not("text" or "markdown" or "html")||e.Description.Length>1048576||e.Title.Length>4096||e.Location.Length>8192||e.LocationUrl.Length>8192||e.Icon.Length>256||e.AnnouncementMessage.Length>65536)return new("invalid");
   var theme=await db.EventThemePreferences.AsNoTracking().Where(t=>t.EventId==id).Select(t=>new{t.ThemeId,t.Mode}).SingleOrDefaultAsync(ct);
   var choices=await db.ThemeCatalogEntries.AsNoTracking().OrderBy(t=>t.Id).Select(t=>new OverviewTheme(t.Id,t.Name)).Take(1025).ToArrayAsync(ct);
   if(theme?.Mode is not(null or 0 or 1 or 2)||choices.Length>1024||choices.Any(t=>string.IsNullOrWhiteSpace(t.Id)||t.Id.Length>128||string.IsNullOrWhiteSpace(t.Name)||t.Name.Length>256)||theme?.ThemeId is{} chosen&&choices.All(t=>t.Id!=chosen))return new("invalid");
   var participantCount=await db.SongParts.AsNoTracking().Where(p=>p.EventId==id&&p.AssignedUserId!=null&&db.Songs.Any(s=>s.Id==p.SongId&&s.EventId==id&&s.DeletedAt==null)&&db.EventPartDefinitions.Any(d=>d.Id==p.PartDefinitionId&&d.EventId==id&&d.DeletedAt==null)).Select(p=>p.AssignedUserId!).Union(db.EventSpectators.AsNoTracking().Where(p=>p.EventId==id).Select(p=>p.UserId)).CountAsync(ct);
   var view=new EventOverviewView(actor,id,e.Title,EventListReader.Instant(e.ScheduledDate)!.Value.ToUniversalTime().ToString("O"),e.OpensAt is null?null:EventListReader.Instant(e.OpensAt)!.Value.ToUniversalTime().ToString("O"),e.Location,e.LocationUrl,e.Icon,e.Capacity,participantCount,e.Description,e.DescriptionFormat,EventDescription.Render(e.Description,e.DescriptionFormat),e.AnnouncementMessage,await db.EventHeaderImages.AsNoTracking().AnyAsync(h=>h.EventId==id,ct),theme?.ThemeId,theme?.Mode is null?null:theme.Mode==0?"system":theme.Mode==1?"light":"dark",choices.OrderBy(t=>Array.IndexOf(new[]{"green","blue","purple","amber","rose","teal","slate","navy","coral","mono"},t.Id) is var rank&&rank>=0?rank:10).ThenBy(t=>t.Id,StringComparer.Ordinal).ToArray());
   await snapshot.CommitAsync(ct);return new("ok",view);
  }finally{await db.Database.CloseConnectionAsync();}
 }
}
public static class EventOverviewEndpoints {
 public static void MapSemEventOverview(this WebApplication app)=>app.MapGet("/api/ui/events/{id}/overview/editor",async(HttpContext c,ApplicationDbContext db,Microsoft.Extensions.Options.IOptions<IdentityOptions> i,TimeProvider t,string id)=>{
  if(id.Length is <1 or >128||c.Request.QueryString.HasValue||c.Request.ContentLength is >0||c.Request.Headers.ContainsKey("Transfer-Encoding"))return Results.BadRequest();
  var r=await EventOverviewReader.ReadAsync(db,c.User,i.Value,id,t.GetUtcNow(),c.RequestAborted);
  return r.State switch{"unauthorized"=>Results.Unauthorized(),"not-found"=>Results.NotFound(),"invalid"=>Results.Problem(statusCode:422,title:"保存済み概要を表示できません。管理者へ確認してください。"),_=>Results.Json(r.View)};
 }).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
}
