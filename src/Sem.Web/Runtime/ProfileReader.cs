using System.Buffers.Binary;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sem.Authentication;
using Sem.Web.Core;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed record PublicSocial(string Service,string Url);
public sealed record PublicProfileAnswer(string Id,string Text,string Type,string? Value);
public sealed record PublicProfileSong(string Id,string Title,string Artist,string[] Parts);
public sealed record PublicRegistration(string Id,string Title,string ScheduledDate,string PhaseName,bool Spectator,PublicProfileSong[] Songs);
public sealed record MemberProfileView(string Id,string SiteName,string DisplayName,string Reading,string CallName,string Icon,bool Own,bool HasImage,PublicSocial[] Social,PublicProfileAnswer[] Answers,PublicRegistration[] Registrations);
public sealed record ProfileReadResult(string State,MemberProfileView? View=null,byte[]? Image=null);
public static class ProfileReader {
 public static async Task<ProfileReadResult> ReadAsync(ApplicationDbContext db,ClaimsPrincipal principal,IdentityOptions identity,string target,DateTimeOffset now,bool image=false,CancellationToken ct=default){
  await db.Database.OpenConnectionAsync(ct);
  try{
   using var snapshot=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);await using var tx=await db.Database.UseTransactionAsync(snapshot,ct);
   if(!await AuthenticationSession.ValidAsync(db,principal,identity,ct))return new("unauthorized");
   var actor=AuthenticationSession.SingleClaim(principal,identity.ClaimsIdentity.UserIdClaimType)!;
   var user=await db.Users.AsNoTracking().Where(u=>u.Id==target&&u.Enabled&&u.EmailConfirmed).Select(u=>new{u.Id,u.DisplayName}).SingleOrDefaultAsync(ct);if(user is null)return new("not-found");
   if(image){var row=await db.ProfileImages.AsNoTracking().Where(p=>p.UserId==target).Select(p=>new{p.Data,p.Width,p.Height}).SingleOrDefaultAsync(ct);if(row is null||row.Data.Length==0)return new("not-found");var bytes=row.Data;
    if(bytes.Length is <24 or >1048576||row.Width is <1 or >512||row.Height is <1 or >512||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||!bytes.AsSpan(12,4).SequenceEqual("IHDR"u8)||BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4))!=row.Width||BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4))!=row.Height)return new("invalid");
    await snapshot.CommitAsync(ct);return new("ok",Image:bytes);
   }
   var profile=await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p=>p.UserId==target,ct);PublicSocial[] social;
   try{if((profile?.SocialJson.Length??0)>32000)return new("invalid");social=JsonSerializer.Deserialize<PublicSocial[]>(profile?.SocialJson??"[]")??[];if(social.Any(s=>s is null||s.Service is null||s.Url is null))return new("invalid");social=social.Where(s=>EventLocationLink.IsSafe(s.Url)).ToArray();}catch(JsonException){return new("invalid");}
   var questions=await db.ProfileQuestions.AsNoTracking().Where(q=>q.Enabled).OrderBy(q=>q.SortOrder).ThenBy(q=>q.Id).Select(q=>new{q.Id,q.Text,q.Type}).ToArrayAsync(ct);
   var values=await db.ProfileAnswers.AsNoTracking().Where(a=>a.UserId==target&&db.ProfileQuestions.Any(q=>q.Id==a.QuestionId&&q.Enabled)).ToDictionaryAsync(a=>a.QuestionId,a=>a.Value,ct);
   if(questions.Any(q=>q.Type is not("Text" or "YesNo")||q.Type=="YesNo"&&values.TryGetValue(q.Id,out var value)&&value is not("YES" or "NO" or "")))return new("invalid");
   // 対象の所属は閲覧者の権限を増やさない。現割当／観覧の候補を先に100件へ絞る。
   var slotQuery=db.SongParts.AsNoTracking().Where(p=>p.AssignedUserId==target&&db.Songs.Any(s=>s.Id==p.SongId&&s.EventId==p.EventId&&s.DeletedAt==null)&&db.EventPartDefinitions.Any(d=>d.Id==p.PartDefinitionId&&d.EventId==p.EventId&&d.DeletedAt==null));
   var ids=await slotQuery.Select(p=>p.EventId).Union(db.EventSpectators.AsNoTracking().Where(s=>s.UserId==target).Select(s=>s.EventId)).ToArrayAsync(ct);
   var dates=await db.SessionEvents.AsNoTracking().Where(e=>ids.Contains(e.Id)).Select(e=>new{e.Id,e.ScheduledDate}).ToArrayAsync(ct);
   var candidates=dates.Where(e=>EventListReader.Instant(e.ScheduledDate)!=null).OrderByDescending(e=>EventListReader.Instant(e.ScheduledDate)).ThenBy(e=>e.Id,StringComparer.Ordinal).Take(100).Select(e=>e.Id).ToArray();
   var admin=await db.Users.AsNoTracking().Where(u=>u.Id==actor).Select(u=>u.IsAdmin).SingleAsync(ct);
   var events=await db.SessionEvents.AsNoTracking().Where(e=>candidates.Contains(e.Id)).Include(e=>e.Phases).Include(e=>e.Admins).ToArrayAsync(ct);
   var visible=events.Select(e=>(Event:e,Phase:EventListReader.Effective(e,now))).Where(x=>x.Phase!=null&&(x.Phase.Kind!="Hidden"||admin||x.Event.Admins.Any(a=>a.UserId==actor))).ToDictionary(x=>x.Event.Id);
   var allowed=visible.Keys.ToArray();var slots=await (from p in slotQuery join s in db.Songs.AsNoTracking() on p.SongId equals s.Id join d in db.EventPartDefinitions.AsNoTracking() on p.PartDefinitionId equals d.Id where allowed.Contains(p.EventId) orderby s.SortOrder,s.Id,d.SortOrder,d.Id,p.Id select new{p.EventId,SongId=s.Id,s.Title,s.Artist,PartName=d.Name}).ToArrayAsync(ct);
   var registrations=candidates.Where(visible.ContainsKey).Select(id=>{var e=visible[id];var songs=slots.Where(p=>p.EventId==id).GroupBy(p=>p.SongId).Select(g=>new PublicProfileSong(g.Key,g.First().Title,g.First().Artist,g.Select(p=>p.PartName).ToArray())).ToArray();return new PublicRegistration(id,e.Event.Title,EventListReader.Instant(e.Event.ScheduledDate)!.Value.ToUniversalTime().ToString("O"),e.Phase!.Kind=="Hidden"?"非公開":e.Phase.Kind=="View"?"閲覧":e.Phase.Name,songs.Length==0,songs);}).ToArray();
   var site=await db.ServiceSettings.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??await db.SiteStates.AsNoTracking().Where(s=>s.Id==1).Select(s=>s.Name).SingleOrDefaultAsync(ct)??"SESSION ROOM";
   var result=new MemberProfileView(user.Id,site,user.DisplayName,profile?.Reading??"",profile?.CallName??"",profile?.Icon??"",actor==target,await db.ProfileImages.AsNoTracking().AnyAsync(p=>p.UserId==target&&p.Width>0&&p.Height>0,ct),social,questions.Select(q=>new PublicProfileAnswer(q.Id,q.Text,q.Type,values.GetValueOrDefault(q.Id))).ToArray(),registrations);
   await snapshot.CommitAsync(ct);return new("ok",result);
  }finally{await db.Database.CloseConnectionAsync();}
 }
}
