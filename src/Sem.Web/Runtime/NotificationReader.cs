using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Authentication;
using Sem.Web.Core;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed record NotificationEvent(string Id,string Title);
public sealed record NotificationPart(string Id,string Kind,string Title,string Start,bool Enabled);
public sealed record NotificationRule(string Kind,bool Configured,bool Enabled);
public sealed record NotificationSelection(string Id,string Title,bool CanManage,NotificationPart[] Parts,NotificationRule[] Rules);
public sealed record NotificationView(string ActorId,string SiteName,NotificationEvent[] Events,NotificationSelection? Selected);
public sealed record NotificationReadResult(string State,NotificationView? View=null);
public static class NotificationReader {
 public static async Task<NotificationReadResult> ReadAsync(ApplicationDbContext db,ClaimsPrincipal principal,IdentityOptions identity,DateTimeOffset now,string? eventId,CancellationToken ct){
  await db.Database.OpenConnectionAsync(ct);
  try{using var snapshot=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);await using var tx=await db.Database.UseTransactionAsync(snapshot,ct);
   if(!await AuthenticationSession.ValidAsync(db,principal,identity,ct))return new("unauthorized");
   var actor=AuthenticationSession.SingleClaim(principal,identity.ClaimsIdentity.UserIdClaimType)!;
   var admin=await db.Users.AsNoTracking().Where(u=>u.Id==actor).Select(u=>u.IsAdmin).SingleAsync(ct);
   var events=await db.SessionEvents.AsNoTracking().Include(e=>e.Phases).Include(e=>e.Admins).ToArrayAsync(ct);
   // 参加／演奏の有無は条件にしない。現在の実効フェーズと担当権限を毎回判定する。
   var visible=events.Where(e=>EventListReader.Effective(e,now) is{} p&&EventListReader.Instant(e.ScheduledDate)!=null&&(p.Kind!="Hidden"||admin||e.Admins.Any(a=>a.UserId==actor))).OrderByDescending(e=>EventListReader.Instant(e.ScheduledDate)).ThenBy(e=>e.Id,StringComparer.Ordinal).ToArray();
   if(visible.Length>4096||visible.Any(e=>e.Id.Length is <1 or >128||e.Title.Length>2000))return new("invalid");
   NotificationSelection? selected=null;
   if(eventId!=null){var e=visible.SingleOrDefault(e=>e.Id==eventId);if(e==null)return new("not-found");
    var parts=await db.TimeParts.AsNoTracking().Where(p=>p.EventId==eventId&&p.DeletedAt==null).ToArrayAsync(ct);
    var songs=await db.Songs.AsNoTracking().Where(s=>s.EventId==eventId&&s.DeletedAt==null).ToArrayAsync(ct);
    if(parts.Length>4096||parts.Count(p=>p.Kind=="Song")!=songs.Length||parts.Where(p=>p.Kind=="Song").Select(p=>p.SongId).Distinct().Count()!=songs.Length)return new("invalid");
    CalculatedTimetable timetable;try{timetable=TimetableCalculator.Calculate(e,parts,songs);}catch(ValidationException){return new("invalid");}
    if(timetable.Rows.Select(p=>p.Id).Distinct().Count()!=timetable.Rows.Length||timetable.Rows.Any(p=>p.Id.Length is <1 or >128||p.Title.Length>2000))return new("invalid");
    var partIds=timetable.Rows.Select(p=>p.Id).ToArray();
    // 本人×現在イベント×現在行だけをSQLで投影。他人の希望、購読・鍵・予約・送信記録は読み出さない。
    var preferences=await db.Set<PartStartPreference>().AsNoTracking().Where(p=>p.EventId==eventId&&p.UserId==actor&&partIds.Contains(p.PartId)).Select(p=>new{p.PartId,p.Enabled}).ToDictionaryAsync(p=>p.PartId,p=>p.Enabled,ct);
    var rules=await db.Set<EventPushRule>().AsNoTracking().Where(r=>r.EventId==eventId).Select(r=>new{r.Kind,r.Enabled}).ToDictionaryAsync(r=>r.Kind,r=>r.Enabled,ct);
    selected=new(e.Id,e.Title,admin||e.Admins.Any(a=>a.UserId==actor),timetable.Rows.Select(p=>new NotificationPart(p.Id,p.Kind,p.Title,p.Start.ToUniversalTime().ToString("O"),preferences.GetValueOrDefault(p.Id))).ToArray(),new[]{"OpeningReminder","SongPreparation"}.Select(k=>new NotificationRule(k,rules.ContainsKey(k),rules.GetValueOrDefault(k))).ToArray());
   }
   var site=await db.ServiceSettings.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??await db.SiteStates.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??"SESSION ROOM";
   await snapshot.CommitAsync(ct);return new("ok",new(actor,site,visible.Select(e=>new NotificationEvent(e.Id,e.Title)).ToArray(),selected));
  }finally{await db.Database.CloseConnectionAsync();}
 }
}
public static class NotificationEndpoints {
 public static void MapSemNotifications(this WebApplication app){
  app.MapGet("/api/ui/notifications",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t)=>Read(c,db,i,t,null)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
  app.MapGet("/api/ui/events/{id}/notifications",(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider t,string id)=>Read(c,db,i,t,id)).RequireRateLimiting(EventDetailEndpoints.ReadPolicy);
 }
 static async Task<IResult> Read(HttpContext c,ApplicationDbContext db,IOptions<IdentityOptions> i,TimeProvider clock,string? id){
  if(c.Request.QueryString.HasValue||c.Request.ContentLength is >0||c.Request.Headers.ContainsKey("Transfer-Encoding")||id is{Length:<1 or >128})return Results.BadRequest();
  var r=await NotificationReader.ReadAsync(db,c.User,i.Value,clock.GetUtcNow(),id,c.RequestAborted);return r.State switch{"unauthorized"=>Results.Unauthorized(),"not-found"=>Results.NotFound(),"invalid"=>Results.Problem(statusCode:422,title:"保存済み通知設定を表示できません。管理者へ確認してください。"),_=>Results.Json(r.View)};
 }
}
