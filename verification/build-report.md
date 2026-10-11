# DXP build report

Commit: 7f598a0c140f84863cdcf337f6200eb4a7ad8214
Run: https://github.com/jp-raub/sem/actions/runs/38168555710
.NET build: success
Docker build: success
Smoke test: success

Authentication and notification end-to-end validation pending.
  Determining projects to restore...
  Restored /home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj (in 2.12 sec).
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(29,35): warning CS8604: Possible null reference argument for parameter 'host' in 'Task SmtpClient.ConnectAsync(string host, int port = 0, SecureSocketOptions options = SecureSocketOptions.Auto, CancellationToken cancellationToken = default(CancellationToken))'. [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(30,89): warning CS8604: Possible null reference argument for parameter 'userName' in 'Task MailService.AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default(CancellationToken))'. [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(30,111): warning CS8604: Possible null reference argument for parameter 'password' in 'Task MailService.AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default(CancellationToken))'. [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
  Sem.Web -> /home/runner/work/sem/sem/src/Sem.Web/bin/Release/net10.0/Sem.Web.dll

Build succeeded.

/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(29,35): warning CS8604: Possible null reference argument for parameter 'host' in 'Task SmtpClient.ConnectAsync(string host, int port = 0, SecureSocketOptions options = SecureSocketOptions.Auto, CancellationToken cancellationToken = default(CancellationToken))'. [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(30,89): warning CS8604: Possible null reference argument for parameter 'userName' in 'Task MailService.AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default(CancellationToken))'. [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(30,111): warning CS8604: Possible null reference argument for parameter 'password' in 'Task MailService.AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default(CancellationToken))'. [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
    3 Warning(s)
    0 Error(s)

Time Elapsed 00:00:06.74
#8 extracting sha256:32bc8786185914d38a44bc89ffeff78e3d7fdfec4fc046bf135b1128e4a5ff70 0.5s done
#8 extracting sha256:ab9e7b356e2d0263a6bb7d624e94a5827efb77c109f345dfb95e7e20c3c7ba2c
#8 extracting sha256:ab9e7b356e2d0263a6bb7d624e94a5827efb77c109f345dfb95e7e20c3c7ba2c 2.0s done
#8 extracting sha256:dab9b05c2cad4729c801647671db67a7f332348208adfd74691be1de6e4396e3
#8 extracting sha256:dab9b05c2cad4729c801647671db67a7f332348208adfd74691be1de6e4396e3 0.0s done
#8 extracting sha256:2c6f72fec6544b6bf7da120d6e4128b4c10856e803c8d998f72f8da51a1cdedd
#8 extracting sha256:2c6f72fec6544b6bf7da120d6e4128b4c10856e803c8d998f72f8da51a1cdedd done
#8 extracting sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b 0.1s
#8 extracting sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b 0.2s done
#8 DONE 12.6s

#9 [build 2/6] WORKDIR /source
#9 DONE 0.0s

#10 [build 3/6] COPY src/Sem.Web/Sem.Web.csproj src/Sem.Web/
#10 DONE 0.0s

#11 [build 4/6] RUN dotnet restore src/Sem.Web/Sem.Web.csproj
#11 0.449   Determining projects to restore...
#11 2.655   Restored /source/src/Sem.Web/Sem.Web.csproj (in 2.01 sec).
#11 DONE 3.9s

#12 [build 5/6] COPY src/Sem.Web/ src/Sem.Web/
#12 DONE 0.1s

#13 [build 6/6] RUN dotnet publish src/Sem.Web/Sem.Web.csproj -c Release --no-restore -o /out
#13 3.470 /source/src/Sem.Web/Runtime/NotificationService.cs(29,35): warning CS8604: Possible null reference argument for parameter 'host' in 'Task SmtpClient.ConnectAsync(string host, int port = 0, SecureSocketOptions options = SecureSocketOptions.Auto, CancellationToken cancellationToken = default(CancellationToken))'. [/source/src/Sem.Web/Sem.Web.csproj]
#13 3.470 /source/src/Sem.Web/Runtime/NotificationService.cs(30,89): warning CS8604: Possible null reference argument for parameter 'userName' in 'Task MailService.AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default(CancellationToken))'. [/source/src/Sem.Web/Sem.Web.csproj]
#13 3.470 /source/src/Sem.Web/Runtime/NotificationService.cs(30,111): warning CS8604: Possible null reference argument for parameter 'password' in 'Task MailService.AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default(CancellationToken))'. [/source/src/Sem.Web/Sem.Web.csproj]
#13 4.035   Sem.Web -> /source/src/Sem.Web/bin/Release/net10.0/Sem.Web.dll
#13 8.965   Sem.Web -> /out/
#13 DONE 9.0s

#14 [stage-1 3/4] COPY --from=build /out/ ./
#14 DONE 0.4s

#15 [stage-1 4/4] RUN mkdir -p /data/keys && chown -R app:app /data
#15 DONE 0.1s

#16 exporting to image
#16 exporting layers
#16 exporting layers 0.4s done
#16 writing image sha256:0152c75434e38cf3a25d6e69b53df3de2ecd77b9e7132d40ac86b5c19c208c9c done
#16 naming to docker.io/library/sem-dxp:ci done
#16 DONE 0.4s
Empty DB creation, untrusted host rejection, restart storage readiness: passed
warn: Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager[35]
      No XML encryptor configured. Key {bf3d9e68-9769-4dda-940a-f51b3e2a7621} may be persisted to storage in unencrypted form.
