from pathlib import Path
p=Path('src/Sem.Web/Runtime/DeploymentSettings.cs')
p.write_text(p.read_text().replace('KnownIPProxies', 'KnownProxies'))
p=Path('src/Sem.Web/Runtime/SemEndpoints.cs')
s=p.read_text().replace('if (action == "read-message" && post) { var m = await db.Messages.FirstOrDefaultAsync(m => m.Id == Text("id", 64) && m.UserId == user.Id);', 'if (action == "read-message" && post) { var messageId = Text("id", 64); var m = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.UserId == user.Id);')
s=s.replace('if (!new EmailAddressAttribute().IsValid(email))', 'if (email.Any(char.IsControl) || !new EmailAddressAttribute().IsValid(email))')
s=s.replace('if (site?.Initialized == true || site is not null && site.PendingUntil > Now) throw new ValidationException("初期設定は完了済み、または登録確認待ちです。");', 'if (site?.Initialized == true) throw new ValidationException("初期設定は完了済みです。");\n                if (site is not null && site.PendingUntil > Now) { var reserved = await users.FindByIdAsync(site.PendingAdminId!); if (reserved?.Email != email) throw new ValidationException("別のメールアドレスで初期登録確認待ちです。"); }')
old='''            if (await users.FindByEmailAsync(email) is not null) throw new ValidationException("そのアドレスでは登録処理を開始できません。");
            var pendingUser = new ApplicationUser { Email = email, UserName = email, DisplayName = display, Enabled = false, IsAdmin = false, Bootstrap = bootstrap, InvitationId = inviteId };
            var result = await users.CreateAsync(pendingUser);
            if (!result.Succeeded) throw new ValidationException("登録情報を確認してください。");'''
new='''            var pendingUser = await users.FindByEmailAsync(email);
            if (pendingUser is not null && (pendingUser.Enabled || pendingUser.Bootstrap != bootstrap || !bootstrap && pendingUser.InvitationId != inviteId)) throw new ValidationException("そのアドレスでは登録処理を開始できません。");
            if (pendingUser is null)
            {
                pendingUser = new ApplicationUser { Email = email, UserName = email, DisplayName = display, Enabled = false, IsAdmin = false, Bootstrap = bootstrap, InvitationId = inviteId };
                var result = await users.CreateAsync(pendingUser);
                if (!result.Succeeded) throw new ValidationException("登録情報を確認してください。");
            }
            else await db.EmailTickets.Where(t => t.UserId == pendingUser.Id && !t.Used).ExecuteUpdateAsync(s => s.SetProperty(t => t.Used, true));'''
s=s.replace(old,new);p.write_text(s)
p=Path('src/Sem.Web/Sem.Web.csproj');p.write_text(p.read_text().replace('Include="MailKit" Version="4.9.0"', 'Include="MailKit" Version="4.16.0"'))
p=Path('src/Sem.Web/Runtime/NotificationService.cs');s=p.read_text().replace('cfg["Smtp:Host"],', 'cfg["Smtp:Host"]!,').replace('cfg["Smtp:Username"], cfg["Smtp:Password"],','cfg["Smtp:Username"]!, cfg["Smtp:Password"] ?? "",');p.write_text(s)
p=Path('src/Sem.Web/wwwroot/sem.js');s=p.read_text().replace('const reg=await navigator.serviceWorker.register(\'/sem-sw.js\');', 'if(Notification.permission!=="granted"&&await Notification.requestPermission()!=="granted")throw Error("通知が許可されませんでした。");const reg=await navigator.serviceWorker.register(\'/sem-sw.js\');').replace('<h2>メッセージボックス</h2>', '<h2 id="sem-inbox">メッセージボックス</h2>').replace("root.innerHTML=html;bindButtons();", "root.innerHTML=html;bindButtons();if(q.get('messages'))document.getElementById('sem-inbox')?.scrollIntoView();");p.write_text(s)
