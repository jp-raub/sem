using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;
public sealed record ReceptionHistoryEntry(string UserId,string DisplayName,bool CurrentlyRegistered,ReceptionHistory Receipt);
public sealed record ReceptionHistoryPage(string EventId,string? UserId,ReceptionHistoryEntry[] Receipts,CommentCursor? Next);
public sealed partial class CoreFoundationService {
 public async Task<ReceptionHistoryPage> ReadReceptionHistoryAsync(ClaimsPrincipal principal,string id,string? userId=null,CommentCursor? before=null,CancellationToken ct=default){
  if(userId is not null&&(string.IsNullOrWhiteSpace(userId)||userId.Length>200))throw new ValidationException("対象者を確認してください。");
  if(before is not null&&(!Guid.TryParse(before.Id,out _)||!DateTimeOffset.TryParseExact(before.At,"yyyy-MM-dd'T'HH:mm:ss.fff'Z'",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out _)))throw new ValidationException("履歴の続きを確認してください。");
  await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);await Managed(db,principal,id,ct);
  // Historical records remain readable by CURRENT event managers after registration withdrawal.
  // The membership marker is a current snapshot, never evidence of a current active receipt.
  var registered=(await Participants(db,id,ct)).ToArray();var rows=db.EventReceptions.AsNoTracking().Where(x=>x.EventId==id);
  if(userId is not null)rows=rows.Where(x=>x.UserId==userId);
  if(before is not null)rows=rows.Where(x=>string.Compare(x.At,before.At)<0||x.At==before.At&&string.Compare(x.Id,before.Id)<0);
  var receipts=await(from r in rows join u in db.Users.AsNoTracking() on r.UserId equals u.Id join a in db.Users.AsNoTracking() on r.ActorId equals a.Id join c in db.Users.AsNoTracking() on r.CancelledBy equals c.Id into cancelled from c in cancelled.DefaultIfEmpty() orderby r.At descending,r.Id descending select new ReceptionHistoryEntry(r.UserId,u.DisplayName,registered.Contains(r.UserId),new ReceptionHistory(r.Id,r.At,r.ActorId,a.DisplayName,r.Method,r.Note,r.CancelledAt,r.CancelledBy,c==null?null:c.DisplayName,r.ReadId))).Take(101).ToArrayAsync(ct);
  var visible=receipts.Take(100).ToArray();var next=receipts.Length>100?new CommentCursor(visible[^1].Receipt.At,visible[^1].Receipt.Id):null;
  await tx.CommitAsync(ct);return new(id,userId,visible,next);
 }
}
