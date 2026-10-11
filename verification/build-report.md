# DXP build report

Commit: db36b5be5a481e0d695ff485a56a15595833994e
Run: https://github.com/jp-raub/sem/actions/runs/38168271156
.NET build: failure
Docker build: skipped

Foundation only; not ready for external deployment.
  Determining projects to restore...
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MailKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-9j88-vvj5-vhgr
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MimeKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-g7hc-96xr-gvvx
  Restored /home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj (in 11.52 sec).
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MailKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-9j88-vvj5-vhgr
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MimeKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-g7hc-96xr-gvvx
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(56,105): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(61,123): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(65,297): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(66,43): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(68,157): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(16,207): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(19,94): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(20,117): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(37,15): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(39,64): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]

Build FAILED.

/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MailKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-9j88-vvj5-vhgr
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MimeKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-g7hc-96xr-gvvx
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(56,105): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(61,123): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(65,297): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(66,43): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(68,157): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(16,207): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(19,94): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/NotificationService.cs(20,117): error CS0103: The name 'SemEndpoints' does not exist in the current context [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(37,15): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(39,64): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
    2 Warning(s)
    10 Error(s)

Time Elapsed 00:00:07.18
