# DXP build report

Commit: d78e85b88a374858d0b0d5417b9aac049fe943d0
Run: https://github.com/jp-raub/sem/actions/runs/38168385877
.NET build: failure
Docker build: skipped

Foundation only; not ready for external deployment.
  Determining projects to restore...
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MailKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-9j88-vvj5-vhgr
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MimeKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-g7hc-96xr-gvvx
  Restored /home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj (in 2.99 sec).
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MailKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-9j88-vvj5-vhgr
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MimeKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-g7hc-96xr-gvvx
/home/runner/work/sem/sem/src/Sem.Web/Runtime/SemEndpoints.cs(207,108): error CS8110: An expression tree may not contain a reference to a local function [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(37,15): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(39,64): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]

Build FAILED.

/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MailKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-9j88-vvj5-vhgr
/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj : warning NU1902: Package 'MimeKit' 4.9.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-g7hc-96xr-gvvx
/home/runner/work/sem/sem/src/Sem.Web/Runtime/SemEndpoints.cs(207,108): error CS8110: An expression tree may not contain a reference to a local function [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(37,15): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(39,64): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
    2 Warning(s)
    3 Error(s)

Time Elapsed 00:00:10.02
