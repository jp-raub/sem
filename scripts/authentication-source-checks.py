#!/usr/bin/env python3
"""認証分離段階の配置境界。実HTTP・暗号検証の代用ではない。"""
from pathlib import Path
root=Path(__file__).resolve().parents[1]
web=root/'src/Sem.Web'
assert 'Sem.Authentication' not in (web/'Sem.Web.csproj').read_text()
assert 'Sem.Storage' not in (web/'Sem.Web.csproj').read_text()
assert 'MapSemNativeAuthentication' not in (web/'Program.cs').read_text()
for project in (root/'src').glob('*/*.csproj'):
    assert 'Sem.Authentication.Fixture' not in project.read_text(), project
workflow=(root/'.github/workflows/main-integration.yml').read_text()
assert 'dotnet run --project tests/Sem.Authentication.Checks -c Release' in workflow
assert 'run: node scripts/authentication-native-browser.cjs' in workflow
assert "if: steps.auth-scope.outputs.storage != 'false'" in workflow
assert 'SEM_AUTH_FIXTURE_DLL: /tmp/sem-auth-fixture/Sem.Authentication.Fixture.dll' in workflow
assert 'AddSemNativeAuthentication' in (root/'src/Sem.Authentication/AuthenticationPipeline.cs').read_text()
assert not (root/'README.md').exists()
print('認証配置: 製品ホスト未接続、合成登録fixtureはtests内のみ、既存モデル・UIは別検査で保全。')
