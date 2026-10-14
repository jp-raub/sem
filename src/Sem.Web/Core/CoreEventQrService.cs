using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
public sealed record EventQrView(string EventId,string UserId,long TokenVersion,string? Token,string Title="",string DisplayName="");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventQrEdit(string RequestId,string? UserId,long TokenVersion,bool Reissue);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventQrReadEdit(string RequestId,string Token);
public sealed record EventQrIssueResult(string RequestId,long TokenVersion,bool Replayed=false);
public sealed record EventQrReadResult(string RequestId,string ReadId,string UserId,long TokenVersion,string At,bool Replayed=false);
public sealed record EventQrHistoryItem(string Id,string UserId,string DisplayName,string ActorId,string ActorName,string At,long TokenVersion);
public sealed record EventQrHistory(string EventId,EventQrHistoryItem[] Reads,CommentCursor? Next);
public sealed partial class CoreFoundationService {
 private IDataProtector EventQrProtector()=> (qrProtection??throw new InvalidOperationException("QR protection unavailable")).CreateProtector("SEM.EventReceptionQR.v1");
 private static async Task<ApplicationUser> QrParticipant(ApplicationDbContext db,string eventId,string userId,CancellationToken ct){
  var user=await db.Users.SingleOrDefaultAsync(x=>x.Id==userId&&x.Enabled&&x.EmailConfirmed,ct);
  if(user is null||!(await Participants(db,eventId,ct)).Contains(userId))throw new ValidationException("このイベントの有効な参加者コードではありません。");return user;
 }
 public async Task<EventQrView> ReadEventQrAsync(ClaimsPrincipal principal,string id,string? subjectId=null,CancellationToken ct=default){
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var(e,actor,_,manager)=await ParticipantEvent(db,principal,id,ct);var userId=subjectId??actor.Id;if(userId!=actor.Id&&!manager)throw new UnauthorizedAccessException();var subject=await QrParticipant(db,id,userId,ct);
  var row=await db.EventParticipantQrs.AsNoTracking().SingleOrDefaultAsync(x=>x.EventId==id&&x.UserId==userId,ct);var token=row is null?null:EventQrProtector().Unprotect(row.ProtectedToken);await tx.CommitAsync(ct);return new(id,userId,row?.RowVersion??0,token,e.Title,subject.DisplayName);
 }
 public async Task<EventQrIssueResult> IssueEventQrAsync(ClaimsPrincipal principal,string id,EventQrEdit edit,CancellationToken ct=default){
  ReceptionId(edit.RequestId);if(edit.TokenVersion<0)throw new ValidationException("コードの版を確認してください。");var hash=SemEndpoints.Hash(JsonSerializer.Serialize(edit));
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var(_,actor,_,manager)=await ParticipantEvent(db,principal,id,ct);var userId=edit.UserId??actor.Id;if(userId!=actor.Id&&!manager)throw new UnauthorizedAccessException();
  var previous=await db.EventQrOperations.SingleOrDefaultAsync(x=>x.Id==edit.RequestId,ct);if(previous is not null){if(previous.EventId!=id||previous.ActorId!=actor.Id||previous.Kind!="Issue"||previous.RequestHash!=hash)throw new ValidationException("操作Idは再利用できません。");return JsonSerializer.Deserialize<EventQrIssueResult>(previous.ResultJson)! with{Replayed=true};}
  await QrParticipant(db,id,userId,ct);var row=await db.EventParticipantQrs.SingleOrDefaultAsync(x=>x.EventId==id&&x.UserId==userId,ct);if((row?.RowVersion??0)!=edit.TokenVersion)throw new CoreConflictException();
  if(row is not null&&!edit.Reissue)throw new ValidationException("既存コードは読取り、変更する場合は再発行を明示してください。");if(row is null&&edit.Reissue)throw new ValidationException("初回は発行を選んでください。");
  var token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));if(row is null){row=new(){EventId=id,UserId=userId};db.EventParticipantQrs.Add(row);}row.RowVersion++;row.TokenHash=SemEndpoints.Hash(token);row.ProtectedToken=EventQrProtector().Protect(token);
  var result=new EventQrIssueResult(edit.RequestId,row.RowVersion);db.EventQrOperations.Add(new(){Id=edit.RequestId,EventId=id,ActorId=actor.Id,Kind="Issue",RequestHash=hash,ResultJson=JsonSerializer.Serialize(result)});Audit(db,actor.Id,edit.Reissue?"EventQrReissued":"EventQrIssued",id+":"+userId);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return result;
 }
 public async Task<EventQrReadResult> RecordEventQrReadAsync(ClaimsPrincipal principal,string id,EventQrReadEdit edit,CancellationToken ct=default){
  ReceptionId(edit.RequestId);var token=Text(edit.Token,64,"参加者コード",true).ToUpperInvariant();if(!Regex.IsMatch(token,"\\A[A-F0-9]{64}\\z"))throw new ValidationException("このイベントの有効な参加者コードではありません。");var hash=SemEndpoints.Hash(JsonSerializer.Serialize(edit));
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);await Managed(db,principal,id,ct);var actor=await Actor(db,principal,ct);
  var previous=await db.EventQrOperations.SingleOrDefaultAsync(x=>x.Id==edit.RequestId,ct);if(previous is not null){if(previous.EventId!=id||previous.ActorId!=actor.Id||previous.Kind!="Read"||previous.RequestHash!=hash)throw new ValidationException("操作Idは再利用できません。");return JsonSerializer.Deserialize<EventQrReadResult>(previous.ResultJson)! with{Replayed=true};}
  var tokenHash=SemEndpoints.Hash(token);var row=await db.EventParticipantQrs.SingleOrDefaultAsync(x=>x.EventId==id&&x.TokenHash==tokenHash,ct);if(row is null)throw new ValidationException("このイベントの有効な参加者コードではありません。");await QrParticipant(db,id,row.UserId,ct);
  var at=Utc(clock.GetUtcNow());db.EventQrReads.Add(new(){Id=edit.RequestId,EventId=id,UserId=row.UserId,ActorId=actor.Id,TokenVersion=row.RowVersion,At=at});var result=new EventQrReadResult(edit.RequestId,edit.RequestId,row.UserId,row.RowVersion,at);db.EventQrOperations.Add(new(){Id=edit.RequestId,EventId=id,ActorId=actor.Id,Kind="Read",RequestHash=hash,ResultJson=JsonSerializer.Serialize(result)});Audit(db,actor.Id,"EventQrRead",edit.RequestId);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return result;
 }
 public async Task<EventQrHistory> ReadEventQrHistoryAsync(ClaimsPrincipal principal,string id,CommentCursor? before=null,CancellationToken ct=default){
  if(before is not null&&(!Guid.TryParse(before.Id,out _)||!DateTimeOffset.TryParseExact(before.At,"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out _)))throw new ValidationException("履歴の続きを確認してください。");
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);await Managed(db,principal,id,ct);var rows=db.EventQrReads.AsNoTracking().Where(x=>x.EventId==id);if(before is not null)rows=rows.Where(x=>string.Compare(x.At,before.At)<0||x.At==before.At&&string.Compare(x.Id,before.Id)<0);
  var reads=await(from r in rows join u in db.Users on r.UserId equals u.Id join a in db.Users on r.ActorId equals a.Id orderby r.At descending,r.Id descending select new EventQrHistoryItem(r.Id,r.UserId,u.DisplayName,r.ActorId,a.DisplayName,r.At,r.TokenVersion)).Take(101).ToArrayAsync(ct);var visible=reads.Take(100).ToArray();var next=reads.Length>100?new CommentCursor(visible[^1].At,visible[^1].Id):null;await tx.CommitAsync(ct);return new(id,visible,next);
 }
}
