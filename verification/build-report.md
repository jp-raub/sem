# DXP build report

Commit: 548e2899118c794b6a16eeeaba8a72b889425ed5
Run: https://github.com/jp-raub/sem/actions/runs/38217123975
.NET build: success
Docker build: success
Smoke test: success
Virtual passkey / SMTP integration: success

Real Apple-device and actual SMTP / Push-provider testing remains manual.
  Determining projects to restore...
  Restored /home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj (in 2.36 sec).
  Sem.Web -> /home/runner/work/sem/sem/src/Sem.Web/bin/Release/net10.0/Sem.Web.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:08.66
#8 extracting sha256:f2b4ef06ff01a67d4bed7657a4ae379a7d07934b7c3e743454a22a0d5275fb2b 1.2s done
#8 extracting sha256:1cd530f65c97a6d091ed29491e0a06b15976dd71a32a2a31b3d6be720de5681a 0.4s done
#8 extracting sha256:ac6f29623db71ff92d8da6791126e0f01e6b58ed621a56fc46bcec31a487f9b9 done
#8 extracting sha256:32bc8786185914d38a44bc89ffeff78e3d7fdfec4fc046bf135b1128e4a5ff70 0.1s
#8 extracting sha256:32bc8786185914d38a44bc89ffeff78e3d7fdfec4fc046bf135b1128e4a5ff70 0.8s done
#8 extracting sha256:ab9e7b356e2d0263a6bb7d624e94a5827efb77c109f345dfb95e7e20c3c7ba2c
#8 extracting sha256:ab9e7b356e2d0263a6bb7d624e94a5827efb77c109f345dfb95e7e20c3c7ba2c 3.3s done
#8 extracting sha256:dab9b05c2cad4729c801647671db67a7f332348208adfd74691be1de6e4396e3
#8 extracting sha256:dab9b05c2cad4729c801647671db67a7f332348208adfd74691be1de6e4396e3 0.1s done
#8 extracting sha256:2c6f72fec6544b6bf7da120d6e4128b4c10856e803c8d998f72f8da51a1cdedd done
#8 extracting sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b
#8 extracting sha256:eae92bbb09aea95984502ab6604c31ed5247c847c4cf3c3e6fd5934e7c6a3f5b 0.3s done
#8 DONE 9.5s

#9 [build 2/6] WORKDIR /source
#9 DONE 0.0s

#10 [build 3/6] COPY src/Sem.Web/Sem.Web.csproj src/Sem.Web/
#10 DONE 0.0s

#11 [build 4/6] RUN dotnet restore src/Sem.Web/Sem.Web.csproj
#11 0.787   Determining projects to restore...
#11 3.084   Restored /source/src/Sem.Web/Sem.Web.csproj (in 1.94 sec).
#11 DONE 3.4s

#12 [build 5/6] COPY src/Sem.Web/ src/Sem.Web/
#12 DONE 0.0s

#13 [build 6/6] RUN dotnet publish src/Sem.Web/Sem.Web.csproj -c Release --no-restore -o /out
#13 7.476   Sem.Web -> /source/src/Sem.Web/bin/Release/net10.0/Sem.Web.dll
#13 15.52   Sem.Web -> /out/
#13 DONE 15.6s

#14 [stage-1 3/4] COPY --from=build /out/ ./
#14 DONE 0.1s

#15 [stage-1 4/4] RUN mkdir -p /data/keys && chown -R app:app /data
#15 DONE 0.1s

#16 exporting to image
#16 exporting layers
#16 exporting layers 0.8s done
#16 writing image sha256:ce15c0082bb28dc1e5c56ff65e5cb500dcc2a0ec81764ac2d224ac99a2661fab done
#16 naming to docker.io/library/sem-dxp:ci done
#16 DONE 0.8s
Empty DB creation, untrusted host rejection, restart storage readiness: passed
warn: Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager[35]
      No XML encryptor configured. Key {702ebb3e-4860-413c-b64f-52315bafe78e} may be persisted to storage in unencrypted form.
warn: Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager[35]
      No XML encryptor configured. Key {c4ed80a4-4cd1-45ec-970d-2e8cbee659cc} may be persisted to storage in unencrypted form.
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
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
warn: Microsoft.AspNetCore.Antiforgery.DefaultAntiforgery[8]
      The 'Cache-Control' and 'Pragma' headers have been overridden and set to 'no-cache, no-store' and 'no-cache' respectively to prevent caching of this response. Any response that uses antiforgery should not be cached.
{
  "passed": 18,
  "checks": [
    "Empty DB exposes protected initialization",
    "Wrong bootstrap secret is rejected",
    "SMTP receives real confirmation mail",
    "Email confirmation alone grants no normal access",
    "Virtual passkey completes first administrator",
    "Email proof is one-use",
    "Template open/password registration is not exposed",
    "RP ID matches configured origin",
    "CSRF token required for mutation",
    "Stored real passkey authenticates after logout",
    "Invite registration delivers email",
    "Ordinary member has no direct issuance",
    "Server rejects member direct issuance",
    "Completed enrollment atomically consumes capacity",
    "Admin sees approval and rejection",
    "Decision notification reaches requester inbox",
    "Scheduled worker creates notification without page activity",
    "No client runtime errors"
  ],
  "errors": []
}