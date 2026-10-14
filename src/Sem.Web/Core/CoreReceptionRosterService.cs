using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
public sealed record ReceptionPerson(string UserId,string DisplayName,string Reading,bool Spectator,string[] Songs);
public sealed record ReceptionRoster(string EventId,string Title,int ParticipantCount,int MatchCount,ReceptionPerson[] People,bool MoreMatches);
public sealed partial class CoreFoundationService {
 internal static string ReceptionSearchKey(string value)=>string.Concat(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(c=>!char.IsWhiteSpace(c)).Select(c=>c is >= 'ァ' and <= 'ヶ'?(char)(c-0x60):c));
 public async Task<ReceptionRoster> ReadReceptionRosterAsync(ClaimsPrincipal principal,string id,string? query=null,CancellationToken ct=default){
  var search=ReceptionSearchKey(Text(query,200,"受付検索"));
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);
  var ids=(await Participants(db,id,ct)).ToArray();
  var names=await(from u in db.Users.AsNoTracking() join p in db.UserProfiles.AsNoTracking() on u.Id equals p.UserId into profiles from p in profiles.DefaultIfEmpty() where ids.Contains(u.Id) select new{u.Id,u.DisplayName,Reading=p==null?"":p.Reading}).ToArrayAsync(ct);
  var songs=await(from part in db.SongParts.AsNoTracking() join s in db.Songs.AsNoTracking() on part.SongId equals s.Id where part.EventId==id&&part.AssignedUserId!=null select new{UserId=part.AssignedUserId!,s.Id,s.Title,s.SortOrder}).Distinct().OrderBy(x=>x.SortOrder).ThenBy(x=>x.Id).ToArrayAsync(ct);
  var spectators=await db.EventSpectators.AsNoTracking().Where(x=>x.EventId==id).Select(x=>x.UserId).ToArrayAsync(ct);
  var matches=names.Where(u=>search.Length==0||ReceptionSearchKey(u.DisplayName).Contains(search,StringComparison.Ordinal)||ReceptionSearchKey(u.Reading).Contains(search,StringComparison.Ordinal)).OrderBy(u=>ReceptionSearchKey(u.DisplayName),StringComparer.Ordinal).ThenBy(u=>u.Id,StringComparer.Ordinal).ToArray();
  var people=matches.Take(100).Select(u=>new ReceptionPerson(u.Id,u.DisplayName,u.Reading,spectators.Contains(u.Id),songs.Where(s=>s.UserId==u.Id).Select(s=>s.Title).ToArray())).ToArray();await tx.CommitAsync(ct);return new(e.Id,e.Title,ids.Length,matches.Length,people,matches.Length>100);
 }
}
