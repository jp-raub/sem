# PKCS transitive dependency security update

Fresh NuGet audit on 2026-10-14 reported the transitive `System.Security.Cryptography.Pkcs` 10.0.0 dependency. Microsoft advisories [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7) and [GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx) list affected 10.0.0–10.0.12 and patched 10.0.13. The first concerns Windows EnvelopedCms.Decode quadratic CPU exhaustion; the second concerns SignedCms.CheckSignature and includes Windows/Linux/macOS.

Add a direct PackageReference to10.0.13 so the transitive vulnerable version is replaced. No audit suppression, warning relaxation, SMTP behavior, auth flow, database migration, secret, deployment or production configuration change. The existing MailKit/EF/app framework versions are not upgraded in this scoped patch.

Verification: forced dependency restore, current-source `dotnet list src/Sem.Web package --vulnerable --include-transitive`, Release warnings-as-errors, all37 .NET suites. A clean audit is only a snapshot of known issues in the queried feeds, not proof of no vulnerabilities. Earlier successful CI on the old dependency set is not a security-clean claim. Presence of a vulnerable package does not by itself establish app-path exploitability or production compromise.

Docker/base runtime security updates and any rebuilt deployment remain separate review/deployment work; this package patch does not certify the whole runtime/image as patched. Do not distribute the older candidate without incorporating this follow-up.
