# DXP build report

Commit: 5d68ac70016f2b638fa2b367207168d40e393fd8
Run: https://github.com/jp-raub/sem/actions/runs/38169151329
.NET build: success
Docker build: success
Smoke test: success
Virtual passkey / SMTP integration: failure

Real Apple-device and actual SMTP / Push-provider testing remains manual.
  Determining projects to restore...
  Restored /home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj (in 2.83 sec).
  Sem.Web -> /home/runner/work/sem/sem/src/Sem.Web/bin/Release/net10.0/Sem.Web.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:12.35
#8 sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b 20.57MB / 20.57MB 0.5s done
#8 extracting sha256:cc5f75342e9df50121ed06b98a4c3c1ef540823947e47b876b2bead3d4de22af 0.5s done
#8 extracting sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a 0.4s done
#8 extracting sha256:32bc8786185914d38a44bc89ffeff78e3d7fdfec4fc046bf135b1128e4a5ff70 0.1s
#8 extracting sha256:32bc8786185914d38a44bc89ffeff78e3d7fdfec4fc046bf135b1128e4a5ff70 0.8s done
#8 extracting sha256:ab9e7b356e2d0263a6bb7d624e94a5827efb77c109f345dfb95e7e20c3c7ba2c
#8 extracting sha256:ab9e7b356e2d0263a6bb7d624e94a5827efb77c109f345dfb95e7e20c3c7ba2c 3.3s done
#8 extracting sha256:dab9b05c2cad4729c801647671db67a7f332348208adfd74691be1de6e4396e3
#8 extracting sha256:dab9b05c2cad4729c801647671db67a7f332348208adfd74691be1de6e4396e3 0.1s done
#8 extracting sha256:2c6f72fec6544b6bf7da120d6e4128b4c10856e803c8d998f72f8da51a1cdedd done
#8 extracting sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b
#8 extracting sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b 0.3s done
#8 DONE 8.7s

#9 [build 2/6] WORKDIR /source
#9 DONE 0.0s

#10 [build 3/6] COPY src/Sem.Web/Sem.Web.csproj src/Sem.Web/
#10 DONE 0.0s

#11 [build 4/6] RUN dotnet restore src/Sem.Web/Sem.Web.csproj
#11 0.812   Determining projects to restore...
#11 3.399   Restored /source/src/Sem.Web/Sem.Web.csproj (in 2.23 sec).
#11 DONE 3.7s

#12 [build 5/6] COPY src/Sem.Web/ src/Sem.Web/
#12 DONE 0.0s

#13 [build 6/6] RUN dotnet publish src/Sem.Web/Sem.Web.csproj -c Release --no-restore -o /out
#13 7.709   Sem.Web -> /source/src/Sem.Web/bin/Release/net10.0/Sem.Web.dll
#13 15.92   Sem.Web -> /out/
#13 DONE 16.0s

#14 [stage-1 3/4] COPY --from=build /out/ ./
#14 DONE 0.1s

#15 [stage-1 4/4] RUN mkdir -p /data/keys && chown -R app:app /data
#15 DONE 0.1s

#16 exporting to image
#16 exporting layers
#16 exporting layers 0.4s done
#16 writing image sha256:0d126d32c918ccdf7b949607464758111bfa897f7b215738ffe2b7bf8f1ee66f done
#16 naming to docker.io/library/sem-dxp:ci done
#16 DONE 0.4s
Empty DB creation, untrusted host rejection, restart storage readiness: passed
warn: Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager[35]
      No XML encryptor configured. Key {14075eb4-855c-4d6c-8108-59354c9c257b} may be persisted to storage in unencrypted form.
warn: Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager[35]
      No XML encryptor configured. Key {ea507728-9f14-4772-9e38-e8a7da711015} may be persisted to storage in unencrypted form.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
page.waitForSelector: Timeout 30000ms exceeded.
Call log:
  - waiting for locator('[data-action=decide-invitation]') to be visible

    at /home/runner/work/sem/sem/scripts/e2e.cjs:2:5848