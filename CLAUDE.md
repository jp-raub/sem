# SEM（Session Event Manager）

.NET 10・ASP.NET Core Identity（パスキー）・EF Core/SQLite。UIは固定mock（素のJS）へ製品APIを接続して実機能化する。

## 正本（ここへ仕様・残件を複製しない）

- 要件：[要件・整合性・テスト観点](https://app.notion.com/p/e9c3e56adead4b938e076ee187b9c9ac)
- 進め方・検証方針・GitHub記述：[SEM｜開発・運用方針](https://app.notion.com/p/eb248f0abafa4ba18a145a46f873857b)
- 現在の作業状態・完了証跡：[統合タスク台帳](https://app.notion.com/p/00d00690b3ec424da5f964101a8b153e)
- 画面と機能接続の順序：`docs/ui/IMPLEMENTATION-MAP.ja.md`、各接続の契約は `docs/ui/*-CONNECTION.ja.md`

## 構成

| 場所 | 内容 |
|---|---|
| `src/Sem.Web` | ホスト。`Runtime/`＝Reader／Endpoint、`ProductUI/`＝製品側JS、`wwwroot/`＝固定mock |
| `src/Sem.Authentication`／`Sem.Storage`／`Sem.Hosting` | 認証、保存モデル・migration、配置設定・起動保全 |
| `tests/Sem.*.Checks` | コンソール形式の検査（`dotnet run --project … -c Release`）。`Sem.Authentication.Fixture`は合成認証ホスト |
| `scripts/` | 静的検査（py）とブラウザー検査（cjs）。`scripts/cd/`は公開候補・更新の検査 |
| `deploy/`・`.github/workflows/` | イメージ・CD。稼働中のworkflowは main-integration／cd-image／cd-observe／claude／claude-code-review |

## 守ること

1. 採用mockが正。`src/Sem.Web/wwwroot/` の171資産（`mock-manifest.json`で固定、`scripts/mock-checks.py`が検査）は変更しない。旧Razor・旧UI向けfull一式を戻さない。
2. 既定配置はdemo。実認証は明示モードだけ。実DB・鍵・volumeに触れない。模擬認証・localStorage・ダミーQRを製品の実動作へ流用せず、未接続を成功表示しない。
3. 認証・データ保護を省かない。秘密値・実メール・実個人情報をコード・PR・Notion・ログへ出さない。検証は合成データだけ。
4. 適用済みmigration・`model-provenance.json`の由来を変更しない。モデル変更は新migrationと差分記録で行う（`/sem-migration`）。
5. 失敗を成功扱いにしない。未実施・ローカル・CI・公開応答・実機・人の受入を区別して報告し、失敗した試行を成功へ流用しない。
6. UIに不可欠な接続・整合性を優先し、独立したバックエンド拡張を先行しない。
7. commit・PR・docs・コメントは日本語優先（識別子・API名・外部エラー原文は英語のまま）。ルートの`README.md`は作らない。
8. PRは小さく、1接続＝1PRを目安に統合する。CI改善は低優先度で、実装を妨げる場合だけ最小限に行う。

## トークンを使いすぎない

- **読まない**：`src/Sem.Storage/Data/Migrations/*.Designer.cs`、`ApplicationDbContextModelSnapshot.cs`（各約160KB）、`wwwroot/qa/`、`wwwroot/*.md`（履歴）。必要なら `grep` で該当行だけを見る。権限設定でも拒否している。
- **出力は末尾だけ**：検証・ビルドは `set -o pipefail; <コマンド> 2>&1 | tail -n 80`（終了コードを失わない）。全接続回帰（約290秒）は区切りだけで、日常は対象検査と該当の集中ブラウザー検査（`SEM_FOCUSED_*`）を使う。
- **モデル**：既定はSonnet。認証・権限・保存の設計は `/model opusplan`。探索・ログ要約はサブエージェント（Haiku、`.claude/settings.json`で設定）。1PRごとに`/clear`。
- 検証の手順は `/sem-verify`、mock→製品UIの接続手順は `/sem-ui-connect`。
