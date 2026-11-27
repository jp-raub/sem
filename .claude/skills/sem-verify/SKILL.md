---
name: sem-verify
description: SEMの変更後に、変更範囲に応じた検証コマンドを選んで実行し、結果を実施／未実施で正確に報告する。コード・docs・workflow・mockの変更の検証、CI失敗の再現に使う。
---

# SEMの検証

CIの `.github/workflows/main-integration.yml` と同じ検査を、変更範囲に応じて選ぶ。出力は `set -o pipefail; … 2>&1 | tail -n 80` で末尾だけ読む。

## 1. 常に実行（Pythonだけ・数秒）

```sh
for s in test-scope-checks browser-ci-checks mock-checks ui-inventory-checks authentication-source-checks; do python3 scripts/$s.py; done
python3 scripts/cd/release_gate_checks.py; python3 scripts/cd/updater_checks.py; python3 scripts/cd/observe_checks.py
```

## 2. 保存基盤を含む検査（`scripts/test-scope.py`が「必要」と判定する変更）

docs・`wwwroot`・mock用スクリプトだけの変更は不要。それ以外（Runtime／ProductUI／Authentication／Storage／Hosting／migration／CI自身／未知のファイル）は実行する。

```sh
python3 scripts/storage-source-checks.py
for p in Storage Authentication Hosting EventList EventDetail Profile Account PublicAnswer Appearance Message InvitationAdministration; do dotnet run --project tests/Sem.$p.Checks -c Release; done
```

検査プロジェクトを追加したら `main-integration.yml` の同じ一覧にも追加する。

## 3. ホスト・ブラウザー（画面・接続UIの変更）

- Playwright 1.56.1を`/tmp/sem-browser`へ導入し `NODE_PATH=/tmp/sem-browser/node_modules`。
- `dotnet publish src/Sem.Web -c Release -o /tmp/sem` → `cd /tmp/sem && dotnet Sem.Web.dll --urls http://127.0.0.1:8765` → `node scripts/runtime-browser.cjs`、`node scripts/ui-inventory-browser.cjs`。
- 認証が絡む場合：`dotnet publish tests/Sem.Authentication.Fixture -c Release -o /tmp/sem-auth-fixture`。`SEM_PRODUCT_DLL=/tmp/sem/Sem.Web.dll SEM_AUTH_FIXTURE_DLL=/tmp/sem-auth-fixture/Sem.Authentication.Fixture.dll` を付けて `node scripts/authentication-native-browser.cjs`、`node scripts/product-authentication-browser.cjs`。
- **日常は集中実行**：`product-authentication-browser.cjs`は `SEM_FOCUSED_MESSAGES=1`、`SEM_FOCUSED_APPEARANCE=1`、`SEM_FOCUSED_NOTIFICATIONS=1`、`SEM_FOCUSED_INVITATIONS=1`、`SEM_FOCUSED_INVITATION_ADMIN=1`、`SEM_FOCUSED_MENU=1`、`SEM_FOCUSED_COMMENT_EDITOR=1`、`SEM_FOCUSED_PROFILE_EDITOR=1`、`SEM_FOCUSED_ACCOUNT_INPUT=1` のどれか1つで該当区画だけ走る。全接続回帰（約290秒）は機能の区切り・認証／権限／保存の変更・公開前だけ。

## 4. 報告の書き方

- 実行した検査と結果（件数・成功／失敗）を書く。**実行していないものは「未実施」と明記**し、理由（例：`dotnet`がない環境）を添える。
- ローカル成功とCI成功、合成HTTPSと実機・公開応答、局所成功と全体成功を混ぜない。
- 失敗した試行は原因・修正・再実行結果を残し、成功へ流用しない。
