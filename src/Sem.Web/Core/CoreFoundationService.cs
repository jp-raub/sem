using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sem.Web.Data;
using Sem.Web.Runtime;
namespace Sem.Web.Core;

public sealed class CoreConflictException() : Exception("ほかの画面で更新されています。最新状態を取得し、変更内容を確認してから保存してください。");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SocialLink(string Service, string Url);
public sealed record ServiceView(string Name, string Description, long RowVersion);
public sealed record ProfileView(string UserId, string DisplayName, string Reading, string CallName, SocialLink[] Social, long RowVersion,
    string Icon = "", ProfileAnswerView[]? Answers = null, string? ImageDataUrl = null, long ImageRowVersion = 0);
public sealed record MemberView(string UserId, string DisplayName);
public sealed record EventView(string Id, string Title, string Description, string Location, string ScheduledDate,
    string? OpensAt, string AnnouncementMessage, string PhaseKind, string[] ManagerIds, long RowVersion, bool CanManage, string ActivePhaseName, string ActivePhaseKind, int MaxPicks, bool ManualPhaseOverride, string LocationUrl = "", string DescriptionFormat = "text", string Icon = "");
public sealed record WorkspaceView(ServiceView Service, ProfileView Profile, string Email, bool IsAdmin, MemberView[] Members, EventView[] Events);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GlobalAdminAccountView(string UserId, string DisplayName, string Email, bool IsAdmin, bool Enabled,
    bool EmailConfirmed, bool Bootstrap, bool IsRecovering, string ConcurrencyStamp, string LastLoginDisplay);
public sealed record GlobalAdminAccountsView(string ActorId, GlobalAdminAccountView[] Accounts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GlobalAdminRoleChange(string UserId, string ExpectedConcurrencyStamp, bool IsAdmin);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GlobalAdminRolesEdit(GlobalAdminRoleChange[] Changes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ServiceEdit(string Name, string Description, long RowVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileEdit(string DisplayName, string Reading, string CallName, SocialLink[] Social, long RowVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventEdit(string Title, string Description, string Location, DateTimeOffset ScheduledDate,
    DateTimeOffset? OpensAt, string AnnouncementMessage, string PhaseKind, string[] ManagerIds, long RowVersion, string? LocationUrl = null, string? DescriptionFormat = null, string? HeaderImageDataUrl = null, string? Icon = null);

public sealed partial class CoreFoundationService(IDbContextFactory<ApplicationDbContext> factory,
    IOptions<IdentityOptions> identityOptions, TimeProvider clock, BusinessNotificationStore businessNotifications, IConfiguration? notificationConfiguration = null, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider? qrProtection = null)
{
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static string Text(string? value, int max, string label, bool required = false)
    {
        var result = (value ?? "").Trim();
        if (result.Length > max || required && result.Length == 0 || result.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ValidationException(label + "を確認してください。");
        return result;
    }
    private async Task<ApplicationUser> Actor(ApplicationDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var session = principal.FindFirstValue(PasskeyAdditionService.SessionClaim);
        var stamp = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        var user = id is null ? null : await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (principal.Identity?.IsAuthenticated != true || user is null || !user.Enabled || !user.EmailConfirmed ||
            string.IsNullOrEmpty(stamp) || stamp != user.SecurityStamp || string.IsNullOrEmpty(session) ||
            await db.RevokedAuthSessions.AnyAsync(s => s.TokenHash == SemEndpoints.Hash(session), ct))
            throw new UnauthorizedAccessException("通常のパスキー認証が必要です。再ログインしてください。");
        return user;
    }
    private static ProfileView Profile(ApplicationUser user, UserProfile? p) => new(user.Id, user.DisplayName,
        p?.Reading ?? "", p?.CallName ?? "", p is null ? [] : JsonSerializer.Deserialize<SocialLink[]>(p.SocialJson)!, p?.RowVersion ?? 0,
        p?.Icon ?? "", []);
    public async Task<WorkspaceView> ReadAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var user = await Actor(db, principal, ct);
        var settings = await db.ServiceSettings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        var name = settings?.Name ?? await db.SiteStates.Where(s => s.Id == 1).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "SEM";
        var profile = await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == user.Id, ct);
        var members = await db.Users.Where(u => u.Enabled && u.EmailConfirmed).OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Select(u => new MemberView(u.Id, u.DisplayName)).ToArrayAsync(ct);
        var events = await db.SessionEvents.AsNoTracking().Include(e => e.Admins).Include(e => e.Phases)
            .Where(e => user.IsAdmin || e.PhaseKind == "View" || e.Admins.Any(a => a.UserId == user.Id))
            .OrderBy(e => e.ScheduledDate).ThenBy(e => e.Id).ToListAsync(ct);
        return new(new(name, settings?.Description ?? "", settings?.RowVersion ?? 0), await ProfileWithImageAsync(db, user, Profile(user, profile), ct), user.Email!,
            user.IsAdmin, members, events.Select(e => new EventView(e.Id, e.Title, e.Description, e.Location,
                e.ScheduledDate, e.OpensAt, e.AnnouncementMessage, e.PhaseKind, e.Admins.Select(a => a.UserId).Order().ToArray(),
                e.RowVersion, user.IsAdmin || e.Admins.Any(a => a.UserId == user.Id), e.Phases.Single(p => p.IsActive).Name,
                e.Phases.Single(p => p.IsActive).Kind, e.Phases.Single(p => p.IsActive).MaxPicks, e.ManualPhaseOverride, e.LocationUrl, e.DescriptionFormat, e.Icon)).ToArray());
    }
    public async Task<GlobalAdminAccountsView> ReadGlobalAdminAccountsAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await Actor(db, principal, ct);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("全体管理者のみ閲覧できます。");
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var recoveringIds = (await db.PasskeyRecoveryOperations.AsNoTracking()
            .Where(x => x.ExpiresAt > now &&
                (x.Status == "Pending" || x.Status == "EmailConfirmed" || x.Status == "HostPrepared"))
            .Select(x => x.UserId).Distinct().ToArrayAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var accounts = await db.Users.AsNoTracking().OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Id, x.DisplayName, x.Email, x.IsAdmin, x.Enabled, x.EmailConfirmed, x.Bootstrap,
                x.ConcurrencyStamp
            }).ToArrayAsync(ct);
        return new(actor.Id, accounts.Select(x => new GlobalAdminAccountView(
            x.Id, x.DisplayName, x.Email ?? "", x.IsAdmin, x.Enabled, x.EmailConfirmed, x.Bootstrap,
            recoveringIds.Contains(x.Id), x.ConcurrencyStamp ?? "", "記録なし")).ToArray());
    }
    public async Task AuthorizeGlobalAdminAccountsAsync(ClaimsPrincipal principal, string expectedActor, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await Actor(db, principal, ct);
        var claimActor = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!actor.IsAdmin || string.IsNullOrWhiteSpace(claimActor) ||
            !string.Equals(actor.Id, expectedActor, StringComparison.Ordinal) ||
            !string.Equals(claimActor, expectedActor, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("全体管理者の操作者確認に失敗しました。");
    }
    public async Task SaveGlobalAdminRolesAsync(ClaimsPrincipal principal, GlobalAdminRolesEdit edit, CancellationToken ct = default)
    {
        var changes = edit.Changes ?? throw new ValidationException("変更内容を確認してください。");
        if (changes.Length > 500 || changes.Any(x => x is null ||
                string.IsNullOrWhiteSpace(x.UserId) || x.UserId.Length > 128 || x.UserId.Any(char.IsControl) ||
                x.ExpectedConcurrencyStamp is null || x.ExpectedConcurrencyStamp.Length > 128 ||
                x.ExpectedConcurrencyStamp.Any(char.IsControl)) ||
            changes.Select(x => x.UserId).Distinct(StringComparer.Ordinal).Count() != changes.Length)
            throw new ValidationException("アカウントの変更内容を確認してください。");

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var actor = await Actor(db, principal, ct);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("全体管理者のみ変更できます。");
        var ids = changes.Select(x => x.UserId).ToArray();
        var targets = await db.Users.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, StringComparer.Ordinal, ct);
        if (targets.Count != changes.Length) throw new CoreConflictException();

        foreach (var change in changes)
        {
            var target = targets[change.UserId];
            if (!string.Equals(target.ConcurrencyStamp ?? "", change.ExpectedConcurrencyStamp, StringComparison.Ordinal))
                throw new CoreConflictException();
            if (target.Bootstrap && target.IsAdmin && !change.IsAdmin)
                throw new ValidationException("Bootstrap 管理者の全体管理者権限は解除できません。");
            if (change.IsAdmin && !target.IsAdmin)
            {
                if (!target.Enabled || !target.EmailConfirmed)
                    throw new ValidationException("未有効またはメール未確認のアカウントには全体管理者権限を付与できません。");
                var now = clock.GetUtcNow().ToUnixTimeSeconds();
                if (await db.PasskeyRecoveryOperations.AnyAsync(x => x.UserId == target.Id &&
                        x.ExpiresAt > now && (x.Status == "Pending" || x.Status == "EmailConfirmed" || x.Status == "HostPrepared"), ct))
                    throw new ValidationException("復旧中のアカウントには全体管理者権限を付与できません。");
            }
        }

        var activeAdminCount = await db.Users.CountAsync(x => x.IsAdmin && x.Enabled && x.EmailConfirmed, ct);
        var finalActiveAdminCount = activeAdminCount;
        foreach (var change in changes)
        {
            var target = targets[change.UserId];
            if (!target.Enabled || !target.EmailConfirmed || target.IsAdmin == change.IsAdmin) continue;
            finalActiveAdminCount += change.IsAdmin ? 1 : -1;
        }
        if (finalActiveAdminCount < 1)
            throw new ValidationException("最後の有効な全体管理者の権限は解除できません。");

        foreach (var change in changes)
        {
            var target = targets[change.UserId];
            if (target.IsAdmin == change.IsAdmin) continue;
            target.IsAdmin = change.IsAdmin;
            target.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            Audit(db, actor.Id, change.IsAdmin ? "GlobalAdminGranted" : "GlobalAdminRevoked", target.Id);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    public async Task<ProfileView> ReadProfileAsync(ClaimsPrincipal principal, string id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await Actor(db, principal, ct);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.Enabled && u.EmailConfirmed, ct)
            ?? throw new UnauthorizedAccessException("プロフィールを閲覧できません。");
        return await ProfileWithAnswersAsync(db, user, ct);
    }
    public async Task SaveServiceAsync(ClaimsPrincipal principal, ServiceEdit edit, CancellationToken ct = default)
    {
        var name = Text(edit.Name, 60, "サービス名", true); var description = Text(edit.Description, 80, "表示説明");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("全体管理者のみ変更できます。");
        var settings = await db.ServiceSettings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if ((settings?.RowVersion ?? 0) != edit.RowVersion) throw new CoreConflictException();
        if (settings is null) { settings = new(); db.ServiceSettings.Add(settings); }
        settings.Name = name; settings.Description = description; settings.RowVersion++;
        var site = await db.SiteStates.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (site is not null) site.Name = name; // Preserve bootstrap-only fields; one authoritative display name.
        Audit(db, actor.Id, "ServiceSettingsUpdated", "1");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task SaveProfileAsync(ClaimsPrincipal principal, ProfileEdit edit, CancellationToken ct = default)
    {
        var name = Text(edit.DisplayName, 60, "表示名", true);
        var reading = Text(edit.Reading, 80, "表示名の読み").Normalize(NormalizationForm.FormKC);
        reading = Regex.Replace(reading, "[ァ-ヶ]", m => ((char)(m.Value[0] - 0x60)).ToString());
        var call = Text(edit.CallName, 60, "対面での呼び").Normalize(NormalizationForm.FormKC);
        if (!Regex.IsMatch(reading, "^[ぁ-ゖー・\\s]*$") || !Regex.IsMatch(call, "^[ぁ-ゖァ-ヶー・\\s]*$"))
            throw new ValidationException("読みはひらがな、対面での呼びはひらがな・カタカナで入力してください。");
        if (edit.Social?.Any(s => s is null) == true) throw new ValidationException("SNS入力を確認してください。");
        if (reading.Length > 80 || call.Length > 60) throw new ValidationException("読み／呼称が長すぎます。");
        var social = (edit.Social ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Url)).Select(s =>
        {
            var service = Text(s.Service, 60, "SNSサービス名", true); var url = Text(s.Url, 500, "SNS URL", true);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
                throw new ValidationException("SNS URLは資格情報を含まないhttp/httpsで指定してください。");
            return new SocialLink(service, uri.AbsoluteUri);
        }).ToArray();
        if (JsonSerializer.Serialize(social).Length > 32000) throw new ValidationException("SNS入力が大きすぎます。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        var profile = await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == actor.Id, ct);
        if ((profile?.RowVersion ?? 0) != edit.RowVersion) throw new CoreConflictException();
        if (profile is null) { profile = new() { UserId = actor.Id }; db.UserProfiles.Add(profile); }
        actor.DisplayName = name; profile.Reading = reading; profile.CallName = call;
        profile.SocialJson = JsonSerializer.Serialize(social); profile.RowVersion++;
        Audit(db, actor.Id, "OwnProfileUpdated", actor.Id);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<string> SaveEventAsync(ClaimsPrincipal principal, string? id, EventEdit edit, CancellationToken ct = default)
    {
        var title = Text(edit.Title, 100, "イベント名", true); var description = Text(edit.Description, 2000, "概要");
        var icon = edit.Icon is null ? null : EventIconText.Validate(edit.Icon);
        var descriptionFormat = edit.DescriptionFormat is null ? null : EventDescription.ValidateFormat(edit.DescriptionFormat);
        var locationUrl = edit.LocationUrl is null ? null : EventLocationLink.Validate(edit.LocationUrl);
        var location = Text(edit.Location, 500, "会場"); var announcement = Text(edit.AnnouncementMessage, 2000, "お知らせ");
        if (edit.ScheduledDate == default || edit.PhaseKind is not ("Hidden" or "View") || edit.OpensAt > edit.ScheduledDate || edit.RowVersion < 0)
            throw new ValidationException("表示状態／開始・開場日時を確認してください。開場と開始の同時刻は指定できます。");
        var managers = (edit.ManagerIds ?? []).Distinct(StringComparer.Ordinal).Order().ToArray();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        var item = id is null ? null : await db.SessionEvents.Include(e => e.Admins).Include(e => e.Phases).SingleOrDefaultAsync(e => e.Id == id, ct);
        if (id is not null && item is null || !actor.IsAdmin && (item is null || !item.Admins.Any(a => a.UserId == actor.Id)))
            throw new UnauthorizedAccessException("このイベントは管理できません。");
        if ((item?.RowVersion ?? 0) != edit.RowVersion) throw new CoreConflictException();
        // Authorization and stale-version rejection precede all native image decoding.
        (byte[] Png, int Width, int Height)? header = edit.HeaderImageDataUrl is { Length: > 0 }
            ? await EventHeaderImageNormalizer.NormalizeAsync(edit.HeaderImageDataUrl, ct) : null;
        if (await db.Users.CountAsync(u => managers.Contains(u.Id) && u.Enabled && u.EmailConfirmed, ct) != managers.Length)
            throw new ValidationException("有効なイベント管理者を選択してください。");
        if (item is null) {
            item = new() { CreatedAt = Utc(clock.GetUtcNow()), ManualPhaseOverride = edit.PhaseKind == "View", ImportHistoryKnown = true };
            item.Phases.Add(new() { EventId = item.Id, Kind = "Hidden", Name = "非表示", SortOrder = 0, IsActive = edit.PhaseKind == "Hidden" });
            item.Phases.Add(new() { EventId = item.Id, Kind = "View", Name = "閲覧", SortOrder = 1, IsActive = edit.PhaseKind == "View" });
            db.SessionEvents.Add(item);
            await CopyPartPresetsAsync(db,item.Id,ct);
        } else {
            var observationNow = clock.GetUtcNow();
            var priorEffectivePhase = item.ManualPhaseOverride
                ? item.Phases.Single(p => p.IsActive)
                : Automatic(item, observationNow);
            CoreEventImportHistory.Observe(item, priorEffectivePhase, observationNow);
        }
        if (id is not null && item.PhaseKind != edit.PhaseKind) {
            foreach (var phase in item.Phases) phase.IsActive = false;
            await db.SaveChangesAsync(ct);
            item.Phases.Single(p => p.Kind == edit.PhaseKind).IsActive = true;
            item.ManualPhaseOverride = true;
        }
        if (id is not null && (item.ScheduledDate != Utc(edit.ScheduledDate) || item.OpensAt != (edit.OpensAt is null ? null : Utc(edit.OpensAt.Value)))) await EnsurePlanEditable(db,id,ct);
        item.Title = title; item.Description = description;
        if (icon is not null) item.Icon = icon;
        if (descriptionFormat is not null) item.DescriptionFormat = descriptionFormat; item.Location = location;
        if (locationUrl is not null) item.LocationUrl = locationUrl; item.ScheduledDate = Utc(edit.ScheduledDate);
        item.OpensAt = edit.OpensAt is null ? null : Utc(edit.OpensAt.Value); item.AnnouncementMessage = announcement;
        item.PhaseKind = edit.PhaseKind; item.RowVersion++;
        db.EventAdmins.RemoveRange(item.Admins.Where(a => !managers.Contains(a.UserId)));
        foreach (var manager in managers.Where(m => !item.Admins.Any(a => a.UserId == m)))
            item.Admins.Add(new() { EventId = item.Id, UserId = manager, GrantedAt = Utc(clock.GetUtcNow()) });
        if (edit.HeaderImageDataUrl is not null)
        {
            var image = await db.EventHeaderImages.SingleOrDefaultAsync(x => x.EventId == item.Id, ct);
            if (header is null) { if (image is not null) db.EventHeaderImages.Remove(image); }
            else {
                if (image is null) { image = new() { EventId = item.Id }; db.EventHeaderImages.Add(image); }
                image.Data = header.Value.Png; image.Width = header.Value.Width; image.Height = header.Value.Height;
            }
        }
        Audit(db, actor.Id, id is null ? "SessionEventCreated" : "SessionEventUpdated", item.Id);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return item.Id;
    }
    private void Audit(ApplicationDbContext db, string actor, string action, string target) => db.Audits.Add(new Audit
        { ActorId = actor, Action = action, EntityId = target, At = clock.GetUtcNow().ToUnixTimeSeconds() });
}
