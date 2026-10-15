using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QRCoder;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;

namespace Sem.Web.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileAnswerEdit(string QuestionId, string? Value);
public sealed record ProfileAnswerView(string QuestionId, string Text, string Type, string? Value, bool Enabled);
public sealed record ProfileQuestionView(string Id, string Text, string Type, bool Enabled, int SortOrder, long RowVersion, bool HasAnswers);
public sealed record ProfileQuestionsView(string ActorId, bool CanManage, ProfileQuestionView[] Questions);
public sealed record ProfileEditorView(string ActorId, ProfileView Profile, ProfileQuestionView[] Questions, bool CanManageQuestions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileEditorEdit(
    string DisplayName, string Reading, string CallName, SocialLink[] Social, string? Icon,
    long RowVersion, ProfileAnswerEdit[] Answers);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileQuestionOrderItem(string Id, long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileQuestionCommand(
    string Operation, string? Id, string? Text, string? Type, long RowVersion,
    ProfileQuestionOrderItem[]? Order = null);

public sealed record ProfileQrStatus(bool Exists, bool Active, string? ExpiresAt, long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileQrIssueEdit(long RowVersion, bool Reissue);
public sealed record ProfileQrIssued(string Token, string ExpiresAt, long RowVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileQrResolveRequest(string Token);

public static class ProfileQrImage
{
    public static string Link(string publicUrl, string token) =>
        new Uri(new Uri(publicUrl.TrimEnd('/') + "/", UriKind.Absolute),
            "profile/share#token=" + Uri.EscapeDataString(token)).AbsoluteUri;
}

public sealed partial class CoreFoundationService
{
    private const int ProfileQuestionTextLimit = 200;
    private const int ProfileAnswerTextLimit = 1000;
    private const int ProfileQuestionLimit = 100;
    private static readonly TimeSpan ProfileQrLifetime = TimeSpan.FromHours(24);

    private static string ProfileQrHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool IsProfileQrToken(string? token) =>
        token is { Length: 64 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static async Task<ProfileView> ProfileWithAnswersAsync(
        ApplicationDbContext db, ApplicationUser user, CancellationToken ct)
    {
        var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == user.Id, ct);
        var questions = await db.ProfileQuestions.AsNoTracking()
            .OrderBy(q => q.SortOrder).ThenBy(q => q.Id).ToArrayAsync(ct);
        var answers = await db.ProfileAnswers.AsNoTracking().Where(a => a.UserId == user.Id)
            .ToDictionaryAsync(a => a.QuestionId, ct);
        var view = Profile(user, profile);
        var visibleQuestions = questions.Where(q => q.Enabled)
            .Select(q => new ProfileAnswerView(q.Id, q.Text, q.Type,
                answers.TryGetValue(q.Id, out var answer) ? answer.Value : null, q.Enabled))
            .ToArray();
        return view with { Answers = visibleQuestions };
    }

    private static async Task<ProfileQuestionView[]> ReadProfileQuestionRowsAsync(
        ApplicationDbContext db, CancellationToken ct)
    {
        var questions = await db.ProfileQuestions.AsNoTracking()
            .OrderBy(q => q.SortOrder).ThenBy(q => q.Id).ToArrayAsync(ct);
        var answeredIds = (await db.ProfileAnswers.AsNoTracking().Select(a => a.QuestionId).Distinct().ToArrayAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        return questions.Select(q => new ProfileQuestionView(
            q.Id, q.Text, q.Type, q.Enabled, q.SortOrder, q.RowVersion, answeredIds.Contains(q.Id))).ToArray();
    }

    public async Task AuthorizeProfileExtensionsRequestAsync(ClaimsPrincipal principal, string action, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await Actor(db, principal, ct);
        if (action == "profile-questions" && !actor.IsAdmin) throw new UnauthorizedAccessException();
        if (action is not ("profile-editor" or "profile-questions" or "profile-qr")) throw new ValidationException("操作を確認してください。");
    }

    public async Task<ProfileEditorView> ReadProfileEditorAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await Actor(db, principal, ct);
        var profile = await ProfileWithAnswersAsync(db, actor, ct);
        return new(actor.Id, profile, await ReadProfileQuestionRowsAsync(db, ct), actor.IsAdmin);
    }

    public async Task<ProfileQuestionsView> ReadProfileQuestionsAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await Actor(db, principal, ct);
        return new(actor.Id, actor.IsAdmin, await ReadProfileQuestionRowsAsync(db, ct));
    }

    public async Task SaveProfileEditorAsync(
        ClaimsPrincipal principal, ProfileEditorEdit edit, CancellationToken ct = default)
    {
        if (edit.RowVersion < 0 || edit.Answers is null || edit.Social is null ||
            edit.Answers.Length > ProfileQuestionLimit || edit.Answers.Any(a => a is null || string.IsNullOrWhiteSpace(a.QuestionId)) ||
            edit.Answers.Select(a => a.QuestionId).Distinct(StringComparer.Ordinal).Count() != edit.Answers.Length)
            throw new ValidationException("プロフィール入力を確認してください。");

        var name = Text(edit.DisplayName, 60, "表示名", true);
        var reading = Text(edit.Reading, 80, "表示名の読み").Normalize(NormalizationForm.FormKC);
        reading = Regex.Replace(reading, "[ァ-ヶ]", m => ((char)(m.Value[0] - 0x60)).ToString());
        var call = Text(edit.CallName, 60, "対面での呼び").Normalize(NormalizationForm.FormKC);
        if (!Regex.IsMatch(reading, "^[ぁ-ゖー・\\s]*$") || !Regex.IsMatch(call, "^[ぁ-ゖァ-ヶー・\\s]*$"))
            throw new ValidationException("読みはひらがな、対面での呼びはひらがな・カタカナで入力してください。");
        var icon = ValidateProfileEmoji(edit.Icon);
        if (edit.Social.Any(s => s is null)) throw new ValidationException("SNS入力を確認してください。");
        var social = edit.Social.Where(s => !string.IsNullOrWhiteSpace(s.Url)).Select(s =>
        {
            var service = Text(s.Service, 60, "SNSサービス名", true);
            var url = Text(s.Url, 500, "SNS URL", true);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
                throw new ValidationException("SNS URLは資格情報を含まないhttp/httpsで指定してください。");
            return new SocialLink(service, uri.AbsoluteUri);
        }).ToArray();

        if (System.Text.Json.JsonSerializer.Serialize(social).Length > 32000)
            throw new ValidationException("SNS入力が大きすぎます。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        var profile = await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == actor.Id, ct);
        if ((profile?.RowVersion ?? 0) != edit.RowVersion) throw new CoreConflictException();
        if (profile is null)
        {
            profile = new() { UserId = actor.Id };
            db.UserProfiles.Add(profile);
        }

        var activeQuestions = await db.ProfileQuestions.Where(q => q.Enabled).ToDictionaryAsync(q => q.Id, ct);
        var answerRows = await db.ProfileAnswers.Where(a => a.UserId == actor.Id).ToDictionaryAsync(a => a.QuestionId, ct);
        foreach (var input in edit.Answers)
        {
            if (!activeQuestions.TryGetValue(input.QuestionId, out var question))
                throw new ValidationException("停止中または存在しない設問には回答できません。");
            var value = (input.Value ?? "").Trim();
            if (value.Length == 0)
            {
                if (answerRows.TryGetValue(question.Id, out var prior)) db.ProfileAnswers.Remove(prior);
                continue;
            }
            if (question.Type == "Text")
            {
                value = Text(value, ProfileAnswerTextLimit, "設問の回答");
            }
            else if (question.Type != "YesNo" || value is not ("YES" or "NO"))
            {
                throw new ValidationException("設問の回答形式を確認してください。");
            }
            if (answerRows.TryGetValue(question.Id, out var answer))
            {
                answer.Value = value;
                answer.UpdatedAt = Utc(clock.GetUtcNow());
            }
            else
            {
                db.ProfileAnswers.Add(new ProfileAnswer
                {
                    UserId = actor.Id, QuestionId = question.Id, Value = value,
                    UpdatedAt = Utc(clock.GetUtcNow())
                });
            }
        }

        actor.DisplayName = name;
        profile.Reading = reading;
        profile.CallName = call;
        profile.Icon = icon;
        profile.SocialJson = System.Text.Json.JsonSerializer.Serialize(social);
        profile.RowVersion++;
        Audit(db, actor.Id, "OwnProfileUpdated", actor.Id);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CoreConflictException();
        }
    }

    private static string ValidateProfileEmoji(string? raw)
    {
        var value = (raw ?? "").Trim();
        if (value.Length == 0) return "";
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value);
        if (Encoding.UTF8.GetByteCount(value) > 64 || CountTextElements(enumerator) != 1)
            throw new ValidationException("プロフィールアイコンは絵文字1文字で指定してください。");
        var hasEmoji = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var code = rune.Value;
            var emoji = code is >= 0x1F300 and <= 0x1FAFF or
                >= 0x2600 and <= 0x26FF or >= 0x2700 and <= 0x27BF or
                >= 0x1F1E6 and <= 0x1F1FF or 0x00A9 or 0x00AE or 0x203C or 0x2049 or
                0x2122 or 0x2139 or 0x3030 or 0x303D or 0x3297 or 0x3299;
            if (emoji) { hasEmoji = true; continue; }
            if (code is 0x200D or 0xFE0E or 0xFE0F or 0x20E3 or >= 0x1F3FB and <= 0x1F3FF) continue;
            throw new ValidationException("プロフィールアイコンは絵文字1文字で指定してください。");
        }
        if (!hasEmoji) throw new ValidationException("プロフィールアイコンは絵文字1文字で指定してください。");
        return value;

        static int CountTextElements(System.Globalization.TextElementEnumerator e)
        {
            var count = 0;
            while (e.MoveNext()) count++;
            return count;
        }
    }

    public async Task<ProfileQuestionsView> SaveProfileQuestionAsync(
        ClaimsPrincipal principal, ProfileQuestionCommand edit, CancellationToken ct = default)
    {
        if (edit.Operation is not ("Add" or "Edit" or "Stop" or "Reorder" or "Delete"))
            throw new ValidationException("設問の操作を確認してください。");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("全体管理者のみ設問を管理できます。");

        if (edit.Operation == "Add")
        {
            if (await db.ProfileQuestions.CountAsync(ct) >= ProfileQuestionLimit) throw new ValidationException("設問数の上限に達しています。");
            var text = Text(edit.Text, ProfileQuestionTextLimit, "設問", true);
            if (edit.Type is not ("Text" or "YesNo")) throw new ValidationException("設問形式を確認してください。");
            var max = await db.ProfileQuestions.Select(q => (int?)q.SortOrder).MaxAsync(ct) ?? -1;
            db.ProfileQuestions.Add(new ProfileQuestion { Text = text, Type = edit.Type, SortOrder = max + 1 });
        }
        else if (edit.Operation == "Reorder")
        {
            var order = edit.Order ?? [];
            var all = await db.ProfileQuestions.OrderBy(q => q.SortOrder).ThenBy(q => q.Id).ToArrayAsync(ct);
            if (order.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id)) || order.Length != all.Length || order.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != all.Length ||
                !order.Select(x => x.Id).ToHashSet(StringComparer.Ordinal).SetEquals(all.Select(q => q.Id)))
                throw new ValidationException("設問の並び順を再読込してください。");
            foreach (var (item, index) in order.Select((x, i) => (x, i)))
            {
                var question = all.Single(q => q.Id == item.Id);
                if (question.RowVersion != item.RowVersion) throw new CoreConflictException();
                question.SortOrder = index;
                question.RowVersion++;
            }
        }
        else
        {
            if (string.IsNullOrEmpty(edit.Id)) throw new ValidationException("設問を選択してください。");
            var question = await db.ProfileQuestions.SingleOrDefaultAsync(q => q.Id == edit.Id, ct)
                ?? throw new ValidationException("設問を再読込してください。");
            if (question.RowVersion != edit.RowVersion) throw new CoreConflictException();
            if (edit.Operation == "Edit")
            {
                if (edit.Type != question.Type) throw new ValidationException("設問形式は作成後に変更できません。");
                question.Text = Text(edit.Text, ProfileQuestionTextLimit, "設問", true);
                question.RowVersion++;
            }
            else if (edit.Operation == "Stop")
            {
                question.Enabled = false;
                question.RowVersion++;
            }
            else
            {
                if (await db.ProfileAnswers.AnyAsync(a => a.QuestionId == question.Id, ct))
                    throw new ValidationException("回答がある設問は削除できません。停止して回答を保持してください。");
                db.ProfileQuestions.Remove(question);
            }
        }

        Audit(db, actor.Id, "ProfileQuestion" + edit.Operation, edit.Id ?? "catalog");
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CoreConflictException();
        }
        return await ReadProfileQuestionsAsync(principal, ct);
    }

    public async Task<ProfileQrStatus> ReadProfileQrStatusAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await Actor(db, principal, ct);
        var row = await db.ProfileQrCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == actor.Id, ct);
        if (row is null) return new(false, false, null, 0);
        var active = DateTimeOffset.Parse(row.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) > clock.GetUtcNow();
        return new(true, active, row.ExpiresAt, row.RowVersion);
    }

    public async Task<ProfileQrIssued> IssueProfileQrAsync(
        ClaimsPrincipal principal, ProfileQrIssueEdit edit, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actor = await Actor(db, principal, ct);
        var row = await db.ProfileQrCredentials.SingleOrDefaultAsync(x => x.UserId == actor.Id, ct);
        if ((row?.RowVersion ?? 0) != edit.RowVersion) throw new CoreConflictException();
        if (row is null && edit.Reissue || row is not null && !edit.Reissue)
            throw new ValidationException("QRコードの状態が変わりました。再読込してください。");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        var expires = now.Add(ProfileQrLifetime);
        if (row is null)
        {
            row = new ProfileQrCredential { UserId = actor.Id, RowVersion = 1 };
            db.ProfileQrCredentials.Add(row);
        }
        else
        {
            row.RowVersion++;
        }
        row.TokenHash = ProfileQrHash(token);
        row.IssuedAt = Utc(now);
        row.ExpiresAt = Utc(expires);
        Audit(db, actor.Id, edit.Reissue ? "ProfileQRLinkReissued" : "ProfileQRLinkIssued", actor.Id);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CoreConflictException();
        }
        return new(token, row.ExpiresAt, row.RowVersion);
    }

    public async Task<string> ReadProfileQrSvgAsync(
        ClaimsPrincipal principal, string token, long version, string publicUrl, CancellationToken ct = default)
    {
        if (!IsProfileQrToken(token) || version <= 0) throw new UnauthorizedAccessException("QRコードを確認できません。");
        await using var db = await factory.CreateDbContextAsync(ct);
        var actor = await Actor(db, principal, ct);
        var row = await db.ProfileQrCredentials.AsNoTracking().SingleOrDefaultAsync(q => q.UserId == actor.Id, ct);
        if (row is null || row.RowVersion != version || row.TokenHash != ProfileQrHash(token) ||
            DateTimeOffset.Parse(row.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) <= clock.GetUtcNow())
            throw new UnauthorizedAccessException("QRコードを確認できません。");
        var link = ProfileQrImage.Link(publicUrl, token);
        using var data = QRCodeGenerator.GenerateQrCode(link, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(5);
    }

    public async Task<ProfileView> ResolveProfileQrAsync(string token, CancellationToken ct = default) =>
        (await ResolveProfileQrShareAsync(token, ct)).Profile;

    public async Task<(ProfileView Profile, string ExpiresAt)> ResolveProfileQrShareAsync(string token, CancellationToken ct = default)
    {
        if (!IsProfileQrToken(token)) throw new UnauthorizedAccessException("プロフィールを閲覧できません。");
        await using var db = await factory.CreateDbContextAsync(ct);
        var hash = ProfileQrHash(token);
        var now = clock.GetUtcNow();
        var row = await db.ProfileQrCredentials.AsNoTracking()
            .SingleOrDefaultAsync(q => q.TokenHash == hash && q.ExpiresAt != "", ct);
        if (row is null ||
            DateTimeOffset.Parse(row.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) <= now)
            throw new UnauthorizedAccessException("プロフィールを閲覧できません。");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(
            u => u.Id == row.UserId && u.Enabled && u.EmailConfirmed, ct);
        if (user is null) throw new UnauthorizedAccessException("プロフィールを閲覧できません。");
        return (await ProfileWithAnswersAsync(db, user, ct), row.ExpiresAt);
    }
}