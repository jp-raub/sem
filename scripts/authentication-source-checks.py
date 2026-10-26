#!/usr/bin/env python3
"""認証製品接続の配置境界。実HTTP・暗号検証の代用ではない。"""
from pathlib import Path
root=Path(__file__).resolve().parents[1]
web=root/'src/Sem.Web'
assert 'Sem.Hosting/Sem.Hosting.csproj' in (web/'Sem.Web.csproj').read_text()
program=(web/'Program.cs').read_text()
assert 'if (product is not null)' in program and 'app.MapSemNativeAuthentication();' in program
assert 'ProductSettings.Load(Environment.GetEnvironmentVariable)' in program
settings=(root/'src/Sem.Hosting/ProductSettings.cs').read_text()
assert 'mode is null or "demo"' in settings and 'SEM_PRODUCT_MIXED_CONFIGURATION' in settings
adapter=(web/'ProductUI/authentication.js').read_text()
assert 'navigator.credentials.get' in adapter and 'PublicKeyCredential.parseRequestOptionsFromJSON' in adapter
assert 'localStorage' not in adapter.replace('// 固定mockのlogin section／header／dialogのDOM・CSSを採用。demo scriptやlocalStorageは実認証に使わない。','')
assert 'sessionStorage' not in adapter
for project in (root/'src').glob('*/*.csproj'):
    assert 'Sem.Authentication.Fixture' not in project.read_text(), project
workflow=(root/'.github/workflows/main-integration.yml').read_text()
assert 'dotnet run --project tests/Sem.Authentication.Checks -c Release' in workflow
assert 'run: node scripts/authentication-native-browser.cjs' in workflow
assert "if: steps.auth-scope.outputs.storage != 'false'" in workflow
assert 'SEM_AUTH_FIXTURE_DLL: /tmp/sem-auth-fixture/Sem.Authentication.Fixture.dll' in workflow
assert 'AddSemNativeAuthentication' in (root/'src/Sem.Authentication/AuthenticationPipeline.cs').read_text()
assert not (root/'README.md').exists()
assert 'run: node scripts/product-authentication-browser.cjs' in workflow
print('認証配置: 明示モードだけ接続、demoと実認証を分離、登録fixtureはtests内のみ。')
