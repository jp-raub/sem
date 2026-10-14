using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Runtime;
namespace Sem.Web.Core;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QrReceptionEdit(string RequestId,string UserId,string ReadId,string Token,long TokenVersion,long EventVersion,long ParticipantVersion,long ReceptionVersion,bool ReceptionDone,string Note);
public sealed partial class CoreFoundationService {
 public async Task<ReceptionResult> CompleteQrReceptionAsync(ClaimsPrincipal principal,string id,QrReceptionEdit edit,CancellationToken ct=default){
  ReceptionId(edit.RequestId);ReceptionId(edit.ReadId);if(!edit.ReceptionDone||edit.TokenVersion<=0||edit.EventVersion<0||edit.ParticipantVersion<0||edit.ReceptionVersion<0)throw new ValidationException("本人・申込状況と必要な受付対応を確認してください。");var token=Text(edit.Token,64,"参加者コード",true).ToUpperInvariant();if(!Regex.IsMatch(token,"\\A[A-F0-9]{64}\\z"))throw new ValidationException("このイベントの有効な参加者コードではありません。");var note=Text(edit.Note,500,"受付メモ");var hash=SemEndpoints.Hash(JsonSerializer.Serialize(edit));
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);var e=await Managed(db,principal,id,ct);var actor=await Actor(db,principal,ct);
  // Same ledger kind is safe because the full strict payload fingerprint binds the method-specific fields.
  var replay=await ReceptionReplay(db,id,actor.Id,edit.RequestId,hash,"Complete",ct);if(replay is not null)return replay;
  await QrParticipant(db,id,edit.UserId,ct);var read=await db.EventQrReads.SingleOrDefaultAsync(x=>x.Id==edit.ReadId&&x.EventId==id&&x.UserId==edit.UserId,ct)??throw new CoreConflictException();var code=await db.EventParticipantQrs.SingleOrDefaultAsync(x=>x.EventId==id&&x.UserId==edit.UserId,ct);
  if(code is null||code.RowVersion!=edit.TokenVersion||read.TokenVersion!=code.RowVersion||code.TokenHash!=SemEndpoints.Hash(token))throw new CoreConflictException();
  var pv=await db.EventParticipantStates.Where(x=>x.EventId==id&&x.UserId==edit.UserId).Select(x=>x.RowVersion).SingleOrDefaultAsync(ct);if(e.RowVersion!=edit.EventVersion||pv!=edit.ParticipantVersion)throw new CoreConflictException();if(await db.EventReceptions.AnyAsync(x=>x.EventId==id&&x.UserId==edit.UserId&&x.CancelledAt==null,ct))throw new CoreConflictException();var state=await ReceptionState(db,id,edit.UserId,edit.ReceptionVersion,ct);state.RowVersion++;
  var at=Utc(clock.GetUtcNow());db.EventReceptions.Add(new(){Id=edit.RequestId,EventId=id,UserId=edit.UserId,ActorId=actor.Id,At=at,Method="QR",ReadId=read.Id,Note=note});db.ReceptionOperations.Add(new(){Id=edit.RequestId,EventId=id,ActorId=actor.Id,ReceptionId=edit.RequestId,Kind="Complete",RequestHash=hash,At=at});Audit(db,actor.Id,"QrReceptionCompleted",edit.RequestId);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new(edit.RequestId,edit.RequestId);
 }
}
