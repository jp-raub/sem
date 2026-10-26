using System.ComponentModel.DataAnnotations;
using System.Buffers.Binary;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Sem.Authentication;
using Sem.Web.Core;
using Sem.Web.Data;
namespace Sem.Web.Runtime;
public sealed record PublicDefinition(string Id,string Name,string Category);
public sealed record PublicSlot(string Id,string SongId,string DefinitionId,string? DisplayName,bool IsOwn,string? UserId);
public sealed record PublicSongEntry(string Title,string[] Parts);
public sealed record PublicParticipant(string Id,string DisplayName,bool Spectator,PublicSongEntry[] Songs);
public sealed record PublicComment(string Id,string DisplayName,string Body,string? CreatedAt);
public sealed record EventDetailView(string Id,string SiteName,string Title,string Icon,string Location,string? LocationUrl,string ScheduledDate,string? OpensAt,
 string DescriptionHtml,string AnnouncementMessage,string PhaseKind,string PhaseName,int MaxPicks,bool CanManage,int? Capacity,
 bool RoundStarts,string ClockFormat,string DurationFormat,int DefaultPrepSeconds,string? PlannedEndAt,CalculatedTimetable Timetable,
 PublicDefinition[] Definitions,PublicSlot[] Slots,PublicParticipant[] Participants,PublicComment[] Comments,int OwnPhaseSongs,bool HasHeader);
public sealed record EventDetailResult(string State,EventDetailView? View=null,byte[]? Image=null);
public static class EventDetailReader
{
 // 概要／名簿／画像のどの入口でも、失効・現権限・実効フェーズを同一snapshot内で再確認する。
 public static async Task<EventDetailResult> ReadAsync(ApplicationDbContext db,ClaimsPrincipal principal,IdentityOptions identity,string id,DateTimeOffset now,bool image=false,CancellationToken ct=default) {
  await db.Database.OpenConnectionAsync(ct);
  try {
   using var snapshot=((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred:true);
   await using var transaction=await db.Database.UseTransactionAsync(snapshot,ct);
   if(!await AuthenticationSession.ValidAsync(db,principal,identity,ct))return new("unauthorized");
   var userId=AuthenticationSession.SingleClaim(principal,identity.ClaimsIdentity.UserIdClaimType)!;
   var admin=await db.Users.AsNoTracking().Where(u=>u.Id==userId).Select(u=>u.IsAdmin).SingleAsync(ct);
   var e=await db.SessionEvents.AsNoTracking().Include(x=>x.Phases).Include(x=>x.Admins).SingleOrDefaultAsync(x=>x.Id==id,ct);
   var phase=e is null?null:EventListReader.Effective(e,now);var manage=e is not null&&(admin||e.Admins.Any(x=>x.UserId==userId));
   if(e is null||phase is null||EventListReader.Instant(e.ScheduledDate) is null||phase.Kind=="Hidden"&&!manage)return new("not-found");
   if(image){var stored=await db.EventHeaderImages.AsNoTracking().Where(x=>x.EventId==id).Select(x=>new{x.Data,x.Width,x.Height}).SingleOrDefaultAsync(ct);
    if(stored is null)return new("not-found");
    // 保存済みの正規化PNGだけを配信する。アップロード／変換はこの段階に含めない。
    var bytes=stored.Data;
    if(bytes.Length is <24 or >1048576||!bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})||!bytes.AsSpan(12,4).SequenceEqual("IHDR"u8)||stored.Width is <1 or >1600||stored.Height is <1 or >1600||BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4))!=stored.Width||BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4))!=stored.Height)return new("invalid");
    await snapshot.CommitAsync(ct);return new("ok",Image:bytes);
   }
   if(e.Description.Length>1048576||e.DefaultPrepSeconds<0||e.Capacity is <0||e.ClockFormat is not("seconds" or "minutes")||e.DurationFormat is not("units" or "colon")||
      e.OpensAt is not null&&(EventListReader.Instant(e.OpensAt) is not{} opening||opening>EventListReader.Instant(e.ScheduledDate))||
      e.PlannedEndAt is not null&&(EventListReader.Instant(e.PlannedEndAt) is not{} end||end<EventListReader.Instant(e.ScheduledDate)))return new("invalid");
   var songs=await db.Songs.AsNoTracking().Where(x=>x.EventId==id&&x.DeletedAt==null).ToArrayAsync(ct);
   var parts=await db.TimeParts.AsNoTracking().Where(x=>x.EventId==id&&x.DeletedAt==null).ToArrayAsync(ct);
   if(parts.Count(x=>x.Kind=="Song")!=songs.Length||parts.Where(x=>x.Kind=="Song").Select(x=>x.SongId).Distinct().Count()!=songs.Length)return new("invalid");
   CalculatedTimetable timetable;try{timetable=TimetableCalculator.Calculate(e,parts,songs);}catch(ValidationException){return new("invalid");}
   var definitions=await db.EventPartDefinitions.AsNoTracking().Where(x=>x.EventId==id&&x.DeletedAt==null).OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).Select(x=>new PublicDefinition(x.Id,x.Name,x.Category)).ToArrayAsync(ct);
   var definitionIds=definitions.Select(x=>x.Id).ToArray();var songIds=songs.Select(x=>x.Id).ToArray();
   var slots=await db.SongParts.AsNoTracking().Where(x=>x.EventId==id&&songIds.Contains(x.SongId)&&definitionIds.Contains(x.PartDefinitionId)).OrderBy(x=>x.Id).Select(x=>new{x.Id,x.SongId,x.PartDefinitionId,x.AssignedUserId,x.PhaseId}).ToArrayAsync(ct);
   var spectators=await db.EventSpectators.AsNoTracking().Where(x=>x.EventId==id).Select(x=>x.UserId).ToArrayAsync(ct);
   var comments=await db.EventComments.AsNoTracking().Where(x=>x.EventId==id).Select(x=>new{x.Id,x.UserId,x.Body,x.CreatedAt}).ToArrayAsync(ct);
   var people=slots.Where(x=>x.AssignedUserId!=null).Select(x=>x.AssignedUserId!).Concat(spectators).Distinct().ToArray();
   var names=await db.Users.AsNoTracking().Where(x=>people.Contains(x.Id)||comments.Select(c=>c.UserId).Contains(x.Id)).Select(x=>new{x.Id,x.DisplayName}).ToDictionaryAsync(x=>x.Id,x=>x.DisplayName,ct);
   string Name(string person)=>names.GetValueOrDefault(person,"不明");
   var roster=people.Select(person=>{var assigned=slots.Where(x=>x.AssignedUserId==person).ToArray();return new PublicParticipant(person,Name(person),assigned.Length==0,
    timetable.Rows.Where(x=>x.SongId!=null&&assigned.Any(a=>a.SongId==x.SongId)).Select(row=>new PublicSongEntry(row.Title,definitions.SelectMany(d=>assigned.Where(a=>a.SongId==row.SongId&&a.PartDefinitionId==d.Id).Select(_=>d.Name)).ToArray())).ToArray());}).OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray();
   string? Utc(string? text)=>text is null?null:EventListReader.Instant(text)?.ToUniversalTime().ToString("O");
   var site=await db.ServiceSettings.AsNoTracking().Where(x=>x.Id==1).Select(x=>x.Name).SingleOrDefaultAsync(ct)??await db.SiteStates.AsNoTracking().Where(x=>x.Id==1).Select(x=>x.Name).SingleOrDefaultAsync(ct)??"SESSION ROOM";
   var result=new EventDetailView(e.Id,site,e.Title,e.Icon,e.Location,EventLocationLink.IsSafe(e.LocationUrl)?e.LocationUrl:null,Utc(e.ScheduledDate)!,Utc(e.OpensAt),EventDescription.Render(e.Description,e.DescriptionFormat),e.AnnouncementMessage,
    phase.Kind,phase.Kind=="Hidden"?"非公開":phase.Kind=="View"?"閲覧":phase.Name,phase.MaxPicks,manage,e.Capacity,e.RoundStarts,e.ClockFormat,e.DurationFormat,e.DefaultPrepSeconds,Utc(e.PlannedEndAt),timetable,definitions.Where(d=>slots.Any(s=>s.PartDefinitionId==d.Id)).ToArray(),
    slots.Select(x=>new PublicSlot(x.Id,x.SongId,x.PartDefinitionId,x.AssignedUserId is{} person?Name(person):null,x.AssignedUserId==userId,x.AssignedUserId)).ToArray(),roster,
    comments.OrderByDescending(x=>EventListReader.Instant(x.CreatedAt)).ThenBy(x=>x.Id,StringComparer.Ordinal).Select(x=>new PublicComment(x.Id,Name(x.UserId),x.Body,Utc(x.CreatedAt))).ToArray(),
    slots.Where(x=>x.AssignedUserId==userId&&x.PhaseId==phase.Id).Select(x=>x.SongId).Distinct().Count(),await db.EventHeaderImages.AsNoTracking().AnyAsync(x=>x.EventId==id,ct));
   await snapshot.CommitAsync(ct);return new("ok",result);
  }finally{await db.Database.CloseConnectionAsync();}
 }
}
