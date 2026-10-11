using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sem.Web.Data;
namespace Sem.Web.Runtime;

public static class SemEndpoints
{
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static void MapSem(this WebApplication app)
    {
        app.MapMethods("/api/{**action}", ["GET", "POST"], async (HttpContext ctx, ApplicationDbContext db, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signin, IAntiforgery csrf, IDataProtectionProvider dp, DeploymentSettings settings, NotificationService notifications) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            try { await Handle(ctx, db, users, signin, csrf, dp.CreateProtector("SEM.Tokens.v1"), settings, notifications); }
            catch (ValidationException e) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
            catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "ページを再読み込みしてください。" }); }
            catch (JsonException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "入力形式を確認してください。" }); }
            catch (UnauthorizedAccessException) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "この操作の権限がありません。" }); }
        });
        app.MapGet("/verify", async (string token, HttpContext ctx, ApplicationDbContext db) =>
        {
            var hash = Hash(token);
            var ticket = await db.EmailTickets.FirstOrDefaultAsync(t => t.Hash == hash && !t.Used && t.Expires > Now);
            if (ticket is null) return Results.Content("確認リンクは無効・期限切れ・使用済みです。", "text/plain", statusCode: 400);
            await using var tx = await db.Database.BeginTransactionAsync();
            var changed = await db.EmailTickets.Where(t => t.Id == ticket.Id && !t.Used && t.Expires > Now).ExecuteUpdateAsync(s => s.SetProperty(t => t.Used, true));
            if (changed != 1) return Results.BadRequest();
            var user = await db.Users.FindAsync(ticket.UserId);
            if (user is null || user.Enabled) return Results.BadRequest();
            user.EmailConfirmed = true;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            await ctx.SignInAsync("SEM.Enrollment", new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "SEM.Enrollment")), new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(20) });
            return Results.Redirect("/?enroll=1");
        });
    }
    static async Task Handle(HttpContext c, ApplicationDbContext db, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signin, IAntiforgery csrf, IDataProtector protector, DeploymentSettings cfg, NotificationService notify)
    {
        var action = c.Request.RouteValues["action"]?.ToString() ?? "";
        var post = HttpMethods.IsPost(c.Request.Method);
        if (post) { if (c.Request.Headers.Origin.ToString() != cfg.PublicUrl.TrimEnd('/')) throw new UnauthorizedAccessException(); await csrf.ValidateRequestAsync(c); }
        var json = post ? await c.Request.ReadFromJsonAsync<JsonElement>() : default;
        string Text(string key, int max = 500, bool required = true)
        {
            var value = json.TryGetProperty(key, out var p) ? p.GetString()?.Trim() ?? "" : "";
            if (value.Length > max || required && value.Length == 0) throw new ValidationException("入力項目を確認してください：" + key);
            return value;
        }
        int Number(string key, int low, int high, int fallback = 1)
        {
            var value = fallback; if (json.TryGetProperty(key, out var p)) { if (!p.TryGetInt32(out value)) throw new ValidationException("整数を指定してください。"); }
            if (value < low || value > high) throw new ValidationException("人数・期間などの範囲を確認してください。");
            return value;
        }
        async Task Reply(object value) => await c.Response.WriteAsJsonAsync(value);
        var user = await users.GetUserAsync(c.User);
        if (user is not { Enabled: true }) user = null;
        var site = await db.SiteStates.FindAsync(1);
        if (action == "csrf" && !post) { await Reply(new { token = csrf.GetAndStoreTokens(c).RequestToken }); return; }
        if (action == "status" && !post)
        {
            var pending = await c.AuthenticateAsync("SEM.Enrollment");
            await Reply(new { initialized = site?.Initialized ?? false, siteName = site?.Name ?? "SEM", emailConfigured = notify.EmailConfigured, pushConfigured = notify.PushConfigured, pushPublicKey = notify.PublicKey, user = user is null ? null : new { user.Id, user.DisplayName, user.Email, isAdmin = user.IsAdmin }, enrolling = pending.Succeeded }); return;
        }
        if ((action == "setup" || action == "register") && post)
        {
            if (!notify.EmailConfigured) throw new ValidationException("SMTPを設定してから登録してください。");
            var email = Text("email", 254).ToLowerInvariant();
            if (email.Any(char.IsControl) || !new EmailAddressAttribute().IsValid(email)) throw new ValidationException("メールアドレスを確認してください。");
            var display = Text("name", 60);
            await using var tx = await db.Database.BeginTransactionAsync();
            string? inviteId = null;
            var bootstrap = action == "setup";
            if (bootstrap)
            {
                var supplied = Text("secret", 512);
                if (cfg.BootstrapSecret.Length < 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(cfg.BootstrapSecret)))) throw new UnauthorizedAccessException();
                site = await db.SiteStates.FindAsync(1);
                if (site?.Initialized == true || site is not null && site.PendingUntil > Now) throw new ValidationException("初期設定は完了済み、または登録確認待ちです。");
            }
            else
            {
                if (site?.Initialized != true) throw new ValidationException("初期設定が必要です。");
                var hash = Hash(Text("invite", 128));
                var invite = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash && !i.Disabled && i.Expires > Now && i.Uses < i.Limit);
                if (invite is null) throw new ValidationException("招待リンクが失効しています。");
                inviteId = invite.Id;
            }
            if (await users.FindByEmailAsync(email) is not null) throw new ValidationException("そのアドレスでは登録処理を開始できません。");
            var pendingUser = new ApplicationUser { Email = email, UserName = email, DisplayName = display, Enabled = false, IsAdmin = false, Bootstrap = bootstrap, InvitationId = inviteId };
            var result = await users.CreateAsync(pendingUser);
            if (!result.Succeeded) throw new ValidationException("登録情報を確認してください。");
            if (bootstrap)
            {
                if (site is null) { site = new SiteState(); db.SiteStates.Add(site); }
                site.Name = Text("siteName", 60); site.PendingAdminId = pendingUser.Id; site.PendingUntil = Now + 86400;
            }
            var token = Secret();
            db.EmailTickets.Add(new EmailTicket { UserId = pendingUser.Id, Hash = Hash(token), Expires = Now + 3600 });
            notify.Email(db, pendingUser.Id, "SEM メールアドレス確認", "次のリンクでメールを確認し、パスキーを登録してください。\n" + new Uri(new Uri(cfg.PublicUrl), "verify?token=" + token));
            await db.SaveChangesAsync(); await tx.CommitAsync(); await Reply(new { message = "確認メールを送信待ちに登録しました。到着後、リンクを開いてパスキーを登録してください。" }); return;
        }
        if (action == "passkey-options" && post)
        {
            var pending = await c.AuthenticateAsync("SEM.Enrollment");
            var id = user?.Id ?? (pending.Succeeded ? pending.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) : null);
            var owner = id is null ? null : await users.FindByIdAsync(id);
            if (owner is null || !owner.EmailConfirmed) throw new UnauthorizedAccessException();
            var options = await signin.MakePasskeyCreationOptionsAsync(new() { Id = owner.Id, Name = owner.UserName!, DisplayName = owner.DisplayName });
            c.Response.ContentType = "application/json"; await c.Response.WriteAsync(options); return;
        }
        if (action == "passkey-complete" && post)
        {
            var pending = await c.AuthenticateAsync("SEM.Enrollment");
            var id = user?.Id ?? (pending.Succeeded ? pending.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) : null);
            var owner = id is null ? null : await users.FindByIdAsync(id);
            if (owner is null || !owner.EmailConfirmed) throw new UnauthorizedAccessException();
            var result = await signin.PerformPasskeyAttestationAsync(Text("credential", 50000));
            if (!result.Succeeded || result.UserEntity.Id != owner.Id) throw new ValidationException("パスキー登録を確認できませんでした。");
            await using var tx = await db.Database.BeginTransactionAsync();
            if (!owner.Enabled)
            {
                if (owner.Bootstrap)
                {
                    site = await db.SiteStates.FindAsync(1);
                    if (site is null || site.Initialized || site.PendingAdminId != owner.Id || site.PendingUntil <= Now) throw new ValidationException("初期登録が失効しました。");
                    site.Initialized = true; site.PendingAdminId = null; owner.IsAdmin = true;
                }
                else
                {
                    var consumed = await db.Invitations.Where(i => i.Id == owner.InvitationId && !i.Disabled && i.Expires > Now && i.Uses < i.Limit).ExecuteUpdateAsync(s => s.SetProperty(i => i.Uses, i => i.Uses + 1));
                    if (consumed != 1) throw new ValidationException("登録途中で招待が失効しました。");
                }
                owner.Enabled = true;
            }
            var added = await users.AddOrUpdatePasskeyAsync(owner, result.Passkey);
            if (!added.Succeeded) throw new ValidationException("パスキーを保存できませんでした。");
            await db.SaveChangesAsync(); await tx.CommitAsync();
            await c.SignOutAsync("SEM.Enrollment");
            await signin.SignInAsync(owner, false);
            await Reply(new { message = "パスキーを登録しました。" }); return;
        }
        if (action == "login-options" && post)
        {
            var owner = await users.FindByEmailAsync(Text("email", 254));
            if (owner is not { Enabled: true }) throw new ValidationException("ログインできませんでした。");
            c.Response.ContentType = "application/json"; await c.Response.WriteAsync(await signin.MakePasskeyRequestOptionsAsync(owner)); return;
        }
        if (action == "login" && post)
        {
            var result = await signin.PasskeySignInAsync(Text("credential", 50000));
            if (!result.Succeeded) throw new ValidationException("パスキーでログインできませんでした。");
            await Reply(new { message = "ログインしました。" }); return;
        }
        if (user is null) throw new UnauthorizedAccessException();
        void Admin() { if (!user.IsAdmin) throw new UnauthorizedAccessException(); }
        void Audit(string operation, string entity) => db.Audits.Add(new Audit { ActorId = user.Id, Action = operation, EntityId = entity, At = Now });
        if (action == "logout" && post) { await signin.SignOutAsync(); await Reply(new { message = "ログアウトしました。" }); return; }
        if (action == "snapshot" && !post)
        {
            var invites = await db.Invitations.Where(i => user.IsAdmin || i.OwnerId == user.Id).ToListAsync();
            var requests = await db.InvitationRequests.Where(r => user.IsAdmin || r.UserId == user.Id).ToListAsync();
            var people = await db.Users.Where(u => u.Enabled).Select(u => new { u.Id, u.DisplayName }).ToListAsync();
            var events = await db.Events.Where(e => user.IsAdmin || e.ManagerId == user.Id || e.Phase != "Hidden").ToListAsync();
            var deliveries = await db.Deliveries.Where(d => user.IsAdmin || d.UserId == user.Id).Select(d => new { d.Id, d.Kind, d.Subject, d.Attempts, d.SentAt, d.Error, d.Due }).ToListAsync();
            await Reply(new { people, requests, events, invites = invites.Select(i => new { i.Id, i.OwnerId, i.IssuerId, i.RequestId, i.Limit, i.Uses, i.Expires, i.Created, i.Disabled, i.RevokedAt, i.RevokedBy, i.RevocationReason, link = i.Disabled || i.Uses >= i.Limit || i.Expires <= Now ? null : new Uri(new Uri(cfg.PublicUrl), "?invite=" + protector.Unprotect(i.ProtectedToken)).ToString() }), messages = await db.Messages.Where(m => m.UserId == user.Id).OrderByDescending(m => m.Created).ToListAsync(), deliveries }); return;
        }
        if (action == "request-invitation" && post)
        {
            if (user.IsAdmin) throw new ValidationException("全体管理者は直接発行してください。");
            var request = new InvitationRequest { UserId = user.Id, Reason = Text("reason"), Count = Number("count", 1, 5), Days = Number("days", 1, 7), Created = Now };
            db.InvitationRequests.Add(request);
            foreach (var admin in await db.Users.Where(u => u.Enabled && u.IsAdmin).ToListAsync()) notify.Message(db, admin.Id, "招待リンク発行リクエスト", user.DisplayName + "：" + request.Reason);
            Audit("InvitationRequested", request.Id); await db.SaveChangesAsync(); await Reply(new { message = "発行をリクエストしました。" }); return;
        }
        if ((action == "issue-invitation" || action == "decide-invitation") && post)
        {
            Admin(); await using var tx = await db.Database.BeginTransactionAsync();
            var ownerId = user.Id; var count = Number("count", 1, 5); var days = Number("days", 1, 7); string? requestId = null;
            if (action == "decide-invitation")
            {
                var r = await db.InvitationRequests.FindAsync(Text("id", 64));
                if (r is null || r.Status != "Pending") throw new ValidationException("依頼は判断済みです。");
                var approve = json.TryGetProperty("approve", out var ap) && ap.GetBoolean();
                r.Status = approve ? "Approved" : "Rejected"; r.DecidedAt = Now; r.DecidedBy = user.Id; ownerId = r.UserId; count = r.Count; days = r.Days; requestId = r.Id;
                notify.Message(db, ownerId, approve ? "招待リクエストが許可されました" : "招待リクエストが拒否されました", "招待管理を確認してください。");
                if (!approve) { Audit("InvitationRejected", r.Id); await db.SaveChangesAsync(); await tx.CommitAsync(); await Reply(new { message = "拒否しました。" }); return; }
            }
            var token = Secret(); var i = new Invitation { TokenHash = Hash(token), ProtectedToken = protector.Protect(token), OwnerId = ownerId, IssuerId = user.Id, RequestId = requestId, Limit = count, Created = Now, Expires = Now + days * 86400 };
            db.Invitations.Add(i); Audit("InvitationIssued", i.Id); await db.SaveChangesAsync(); await tx.CommitAsync(); await Reply(new { message = "招待リンクを発行しました。" }); return;
        }
        if (action == "revoke-invitation" && post)
        {
            Admin(); var i = await db.Invitations.FindAsync(Text("id", 64)); if (i is null) throw new ValidationException("リンクがありません。");
            i.Disabled = true; i.RevokedBy = user.Id; i.RevokedAt = Now; i.RevocationReason = Text("reason"); Audit("InvitationRevoked", i.Id); await db.SaveChangesAsync(); await Reply(new { message = "即時失効しました。" }); return;
        }
        if (action == "read-message" && post) { var messageId = Text("id", 64); var m = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.UserId == user.Id); if (m is not null) { m.ReadAt = Now; await db.SaveChangesAsync(); } await Reply(new { message = "既読にしました。" }); return; }
        if (action == "test-email" && post) { Admin(); if (!notify.EmailConfigured) throw new ValidationException("SMTP未設定です。"); notify.Email(db, user.Id, "SEM テストメール", "実際のSMTP配信テストです。"); await db.SaveChangesAsync(); await Reply(new { message = "テストメールを送信待ちに登録しました。" }); return; }
        if (action == "subscribe" && post)
        {
            if (!notify.PushConfigured) throw new ValidationException("Push配信鍵が未設定です。");
            var endpoint = Text("endpoint", 2048); if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !new[] { "web.push.apple.com", "fcm.googleapis.com", "updates.push.services.mozilla.com" }.Contains(uri.Host) || uri.Port != 443 || uri.UserInfo.Length != 0) throw new ValidationException("許可されていないPush接続先です。");
            var existing = await db.PushDevices.FirstOrDefaultAsync(d => d.Endpoint == endpoint);
            if (existing is not null && existing.UserId != user.Id) db.PushDevices.Remove(existing);
            if (existing is null || existing.UserId != user.Id) { existing = new PushDevice { UserId = user.Id, Endpoint = endpoint }; db.PushDevices.Add(existing); }
            existing.P256dh = Text("p256dh", 512); existing.Auth = Text("auth", 512); await db.SaveChangesAsync(); await Reply(new { message = "端末を登録しました。" }); return;
        }
        if (action == "unsubscribe" && post) { var endpoint = Text("endpoint", 2048); await db.PushDevices.Where(d => d.UserId == user.Id && d.Endpoint == endpoint).ExecuteDeleteAsync(); await Reply(new { message = "端末の通知を解除しました。" }); return; }
        if (action == "test-push" && post) { if (!notify.PushConfigured) throw new ValidationException("Push未設定です。"); notify.Message(db, user.Id, "SEM テスト通知", "Pushをタップしてメッセージボックスへ戻ることを確認してください。"); await db.SaveChangesAsync(); await Reply(new { message = "通知を送信待ちに登録しました。" }); return; }
        if (action == "create-event" && post) { Admin(); var e = new TestEvent { Title = Text("title", 100), ManagerId = Text("managerId", 64), Starts = json.GetProperty("starts").GetInt64(), Capacity = Number("capacity", 1, 500, 20) }; if (!await db.Users.AnyAsync(u => u.Id == e.ManagerId && u.Enabled)) throw new ValidationException("管理者を確認してください。"); if (json.TryGetProperty("opens", out var op) && op.ValueKind == JsonValueKind.Number) e.Opens = op.GetInt64(); if (e.Opens >= e.Starts) throw new ValidationException("開場は開始より前に指定してください。"); db.Events.Add(e); Audit("EventCreated", e.Id); await db.SaveChangesAsync(); await Reply(new { message = "イベントを作成しました。" }); return; }
        if (action == "publish-event" && post) { var e = await db.Events.FindAsync(Text("id", 64)); if (e is null || !user.IsAdmin && e.ManagerId != user.Id) throw new UnauthorizedAccessException(); e.Phase = "Custom"; await db.SaveChangesAsync(); await Reply(new { message = "参加受付を開始しました。" }); return; }
        if (action == "join-event" && post)
        {
            var e = await db.Events.FindAsync(Text("id", 64)); if (e is null || e.Phase != "Custom") throw new ValidationException("参加受付中ではありません。");
            await using var tx = await db.Database.BeginTransactionAsync();
            if (await db.Participations.AnyAsync(p => p.EventId == e.Id && p.UserId == user.Id) || await db.Participations.CountAsync(p => p.EventId == e.Id) >= e.Capacity) throw new ValidationException("登録済み、または定員に達しています。");
            db.Participations.Add(new Participation { EventId = e.Id, UserId = user.Id });
            notify.Message(db, user.Id, "イベント参加登録", e.Title + "に登録しました。");
            db.Deliveries.Add(new Delivery { UserId = user.Id, Kind = "Reminder", Subject = e.Id, Due = (e.Opens ?? e.Starts) - 7200 });
            await db.SaveChangesAsync(); await tx.CommitAsync(); await Reply(new { message = "参加登録しました。" }); return;
        }
        if (action == "schedule-test" && post) { var seconds = Number("seconds", 10, 600, 30); db.Deliveries.Add(new Delivery { UserId = user.Id, Kind = "ScheduledTest", Subject = "予約通知テスト", Due = Now + seconds }); await db.SaveChangesAsync(); await Reply(new { message = seconds + "秒後の通知を予約しました。画面を閉じて受信を確認してください。" }); return; }
        c.Response.StatusCode = 404; await Reply(new { error = "操作が見つかりません。" });
    }
}
