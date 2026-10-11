# DXP build report

Commit: 2ba7d4d84b89889c46dfe0dca11f994717c8dfb1
Run: https://github.com/jp-raub/sem/actions/runs/38167823777
.NET build: failure
Docker build: skipped

Foundation only; not ready for external deployment.
  Determining projects to restore...
  Restored /home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj (in 3.49 sec).
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(37,15): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(39,64): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]

Build FAILED.

/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(37,15): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
/home/runner/work/sem/sem/src/Sem.Web/Runtime/DeploymentSettings.cs(39,64): error CS1061: 'ForwardedHeadersOptions' does not contain a definition for 'KnownIPProxies' and no accessible extension method 'KnownIPProxies' accepting a first argument of type 'ForwardedHeadersOptions' could be found (are you missing a using directive or an assembly reference?) [/home/runner/work/sem/sem/src/Sem.Web/Sem.Web.csproj]
    0 Warning(s)
    2 Error(s)

Time Elapsed 00:00:08.74
