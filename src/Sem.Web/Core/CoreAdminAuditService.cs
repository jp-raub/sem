using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
namespace Sem.Web.Core;

// Projection of the generic audit ledger only. No message bodies, credentials, or recovery proofs.
public sealed record AdminAuditEntry(string Id,long At,string Actor,string Entity,string Action,string Detail);
public sealed record AdminAuditPage(AdminAuditEntry[] Entries,bool HasMore);
public sealed partial class CoreFoundationService
{
    public async Task<AdminAuditPage> ReadAdminAuditAsync(ClaimsPrincipal principal, int limit = 100, CancellationToken ct = default)
    {
        if(limit is <1 or >200) throw new ValidationException("監査の取得件数は1〜200件で指定してください。");
        await using var db=await factory.CreateDbContextAsync(ct);
        var actor=await Actor(db,principal,ct);
        if(!actor.IsAdmin)throw new UnauthorizedAccessException("全体管理者のみ監査を閲覧できます。");
        var rows=await (from audit in db.Audits.AsNoTracking()
            join user in db.Users.AsNoTracking() on audit.ActorId equals user.Id into actors
            from user in actors.DefaultIfEmpty()
            orderby audit.At descending,audit.Id descending
            select new {audit.Id,audit.At,audit.ActorId,Name=user==null?null:user.DisplayName,audit.Action,audit.EntityId})
            .Take(limit+1).ToArrayAsync(ct);
        return new(rows.Take(limit).Select(x=>new AdminAuditEntry(x.Id,x.At,x.Name??(x.ActorId=="system"?"システム":"削除済み・不明な操作者"),Entity(x.Action),ActionLabel(x.Action),
            SensitiveAction(x.Action)?"認証操作の識別子・秘密情報は表示しません。":$"対象ID：{x.EntityId}（追加の詳細本文は記録なし）")).ToArray(),rows.Length>limit);
    }
    static bool SensitiveAction(string action)=>action.StartsWith("Passkey",StringComparison.Ordinal)||action.StartsWith("Email",StringComparison.Ordinal)||action.StartsWith("Recovery",StringComparison.Ordinal);
    static string Entity(string action)=>SensitiveAction(action)?"認証・アカウント":action.StartsWith("Invitation",StringComparison.Ordinal)?"招待":action.StartsWith("Service",StringComparison.Ordinal)?"サービス":action.StartsWith("OwnProfile",StringComparison.Ordinal)?"プロフィール":action.StartsWith("GlobalAdmin",StringComparison.Ordinal)?"全体管理者権限":"イベント・業務データ";
    static string ActionLabel(string action)=>action switch {
        "SessionEventCreated"=>"イベント作成","SessionEventUpdated"=>"イベント更新","ServiceSettingsUpdated"=>"サービス設定更新","OwnProfileUpdated"=>"本人プロフィール更新","GlobalAdminGranted"=>"全体管理者付与","GlobalAdminRevoked"=>"全体管理者解除","InvitationRequested"=>"招待依頼作成","InvitationApproved"=>"招待依頼承認","InvitationRejected"=>"招待依頼拒否","InvitationRevoked"=>"招待失効",_=>action};
}
