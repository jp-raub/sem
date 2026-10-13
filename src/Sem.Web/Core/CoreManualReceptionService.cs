using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
public sealed record ReceptionHistory(string Id,string At,string ActorId,string ActorName,string Method,string Note,string? CancelledAt,string? CancelledBy,string? CancelledByName,string? ReadId=null);
public sealed record ReceptionTarget(string EventId,string Title,long EventVersion,ReceptionPerson Person,long ParticipantVersion,long ReceptionVersion,ReceptionHistory? Current,ReceptionHistory[] History,bool MoreHistory);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManualReceptionEdit(string RequestId,string UserId,long EventVersion,long ParticipantVersion,long ReceptionVersion,bool ReceptionDone,string Note);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReceptionCancelEdit(string RequestId,string UserId,string ReceptionId,long ReceptionVersion,bool Confirmed);
public sealed record ReceptionResult(string RequestId,string ReceptionId,bool Replayed=false);
public sealed partial class CoreFoundationService {
 private static async Task RequireReceptionParticipant(ApplicationDbContext db,string id,string userId,CancellationToken ct){
  if(string.IsNullOrWhiteSpace(userId)||!(await Participants(db,id,ct)).Contains(userId))throw new CoreConflictException();
 }
 public async Task<ReceptionTarget> ReadReceptionTargetAsync(ClaimsPrincipal principal,string id,string userId,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);await RequireReceptionParticipant(db,id,userId,ct);
  var user=await db.Users.AsNoTracking().SingleAsync(x=>x.Id==userId,ct);var reading=await db.UserProfiles.Where(x=>x.UserId==userId).Select(x=>x.Reading).SingleOrDefaultAsync(ct)??"";
  var songs=await(from p in db.SongParts join s in db.Songs on p.SongId equals s.Id where p.EventId==id&&p.AssignedUserId==userId orderby s.SortOrder,s.Id select s.Title).Distinct().ToArrayAsync(ct);
  var person=new ReceptionPerson(userId,user.DisplayName,reading,await db.EventSpectators.AnyAsync(x=>x.EventId==id&&x.UserId==userId,ct),songs);
  var rows=await(from r in db.EventReceptions.AsNoTracking() join a in db.Users on r.ActorId equals a.Id join c in db.Users on r.CancelledBy equals c.Id into cancelled from c in cancelled.DefaultIfEmpty() where r.EventId==id&&r.UserId==userId orderby r.At descending,r.Id descending select new ReceptionHistory(r.Id,r.At,r.ActorId,a.DisplayName,r.Method,r.Note,r.CancelledAt,r.CancelledBy,c==null?null:c.DisplayName,r.ReadId)).Take(101).ToArrayAsync(ct);
  // Active receipt is fetched independently; it must not disappear behind a bounded history.
  var active=await(from r in db.EventReceptions.AsNoTracking() join a in db.Users on r.ActorId equals a.Id where r.EventId==id&&r.UserId==userId&&r.CancelledAt==null select new ReceptionHistory(r.Id,r.At,r.ActorId,a.DisplayName,r.Method,r.Note,null,null,null,r.ReadId)).SingleOrDefaultAsync(ct);
  var pv=await db.EventParticipantStates.Where(x=>x.EventId==id&&x.UserId==userId).Select(x=>x.RowVersion).SingleOrDefaultAsync(ct);var rv=await db.EventReceptionStates.Where(x=>x.EventId==id&&x.UserId==userId).Select(x=>x.RowVersion).SingleOrDefaultAsync(ct);
  await tx.CommitAsync(ct);return new(id,e.Title,e.RowVersion,person,pv,rv,active,rows.Take(100).ToArray(),rows.Length>100);
 }
 private static void ReceptionId(string id){if(!Guid.TryParse(id,out _))throw new ValidationException("操作Idを確認してください。");}
 private static async Task<EventReceptionState> ReceptionState(ApplicationDbContext db,string id,string userId,long version,CancellationToken ct){
  var state=await db.EventReceptionStates.SingleOrDefaultAsync(x=>x.EventId==id&&x.UserId==userId,ct);if(version<0||(state?.RowVersion??0)!=version)throw new CoreConflictException();
  if(state is null){state=new(){EventId=id,UserId=userId};db.EventReceptionStates.Add(state);}return state;
 }
 private static async Task<ReceptionResult?> ReceptionReplay(ApplicationDbContext db,string id,string actor,string request,string hash,string kind,CancellationToken ct){
  var old=await db.ReceptionOperations.SingleOrDefaultAsync(x=>x.Id==request,ct);if(old is null)return null;
  if(old.EventId!=id||old.ActorId!=actor||old.RequestHash!=hash||old.Kind!=kind)throw new ValidationException("操作Idは再利用できません。");return new(request,old.ReceptionId,true);
 }
 public async Task<ReceptionResult> CompleteManualReceptionAsync(ClaimsPrincipal principal,string id,ManualReceptionEdit edit,CancellationToken ct=default){
  ReceptionId(edit.RequestId);if(!edit.ReceptionDone||edit.EventVersion<0||edit.ParticipantVersion<0||edit.ReceptionVersion<0)throw new ValidationException("本人確認と必要な受付対応を確認してください。");var note=Text(edit.Note,500,"受付メモ");var hash=SemEndpoints.Hash(JsonSerializer.Serialize(edit));
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);var actor=await Actor(db,principal,ct);
  // Current manager/session is mandatory even for a historical idempotent result.
  var replay=await ReceptionReplay(db,id,actor.Id,edit.RequestId,hash,"Complete",ct);if(replay is not null)return replay;
  await RequireReceptionParticipant(db,id,edit.UserId,ct);if(!await db.Users.AnyAsync(x=>x.Id==edit.UserId&&x.Enabled&&x.EmailConfirmed,ct))throw new ValidationException("有効な対象者を選択してください。");
  var pv=await db.EventParticipantStates.Where(x=>x.EventId==id&&x.UserId==edit.UserId).Select(x=>x.RowVersion).SingleOrDefaultAsync(ct);if(e.RowVersion!=edit.EventVersion||pv!=edit.ParticipantVersion)throw new CoreConflictException();
  if(await db.EventReceptions.AnyAsync(x=>x.EventId==id&&x.UserId==edit.UserId&&x.CancelledAt==null,ct))throw new CoreConflictException();var state=await ReceptionState(db,id,edit.UserId,edit.ReceptionVersion,ct);state.RowVersion++;
  var at=Utc(clock.GetUtcNow());db.EventReceptions.Add(new(){Id=edit.RequestId,EventId=id,UserId=edit.UserId,ActorId=actor.Id,At=at,Note=note});db.ReceptionOperations.Add(new(){Id=edit.RequestId,EventId=id,ActorId=actor.Id,ReceptionId=edit.RequestId,Kind="Complete",RequestHash=hash,At=at});Audit(db,actor.Id,"ManualReceptionCompleted",edit.RequestId);
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(edit.RequestId,edit.RequestId);
 }
 public async Task<ReceptionResult> CancelReceptionAsync(ClaimsPrincipal principal,string id,ReceptionCancelEdit edit,CancellationToken ct=default){
  ReceptionId(edit.RequestId);ReceptionId(edit.ReceptionId);if(!edit.Confirmed||edit.ReceptionVersion<0)throw new ValidationException("受付取消しを確認してください。");var hash=SemEndpoints.Hash(JsonSerializer.Serialize(edit));
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);await Managed(db,principal,id,ct);var actor=await Actor(db,principal,ct);var replay=await ReceptionReplay(db,id,actor.Id,edit.RequestId,hash,"Cancel",ct);if(replay is not null)return replay;
  await RequireReceptionParticipant(db,id,edit.UserId,ct);var receipt=await db.EventReceptions.SingleOrDefaultAsync(x=>x.Id==edit.ReceptionId&&x.EventId==id&&x.UserId==edit.UserId&&x.CancelledAt==null,ct)??throw new CoreConflictException();var state=await ReceptionState(db,id,edit.UserId,edit.ReceptionVersion,ct);state.RowVersion++;
  var at=Utc(clock.GetUtcNow());receipt.CancelledAt=at;receipt.CancelledBy=actor.Id;db.ReceptionOperations.Add(new(){Id=edit.RequestId,EventId=id,ActorId=actor.Id,ReceptionId=receipt.Id,Kind="Cancel",RequestHash=hash,At=at});Audit(db,actor.Id,"ReceptionCancelled",receipt.Id);
  await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(edit.RequestId,receipt.Id);
 }
}
