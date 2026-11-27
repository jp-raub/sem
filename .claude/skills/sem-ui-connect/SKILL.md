---
name: sem-ui-connect
description: 固定mockの画面を製品の実データ（参照→限定した保存）へ接続する手順。新しいUI接続のPRを始めるときに手動で呼び出す。
disable-model-invocation: true
---

# mock→製品UIの接続手順

参照実装：PR #143（本人メッセージ。`docs/ui/MESSAGE-READ-CONNECTION.ja.md`、`Runtime/MessageReader.cs`、`ProductUI/message-view.js`、`scripts/message-browser.cjs`）。同様の既存接続は `docs/ui/*-CONNECTION.ja.md` を参照。

## 始める前

1. `docs/ui/IMPLEMENTATION-MAP.ja.md` の接続順・チェックリストで対象と前提を確認し、要件ID（Notion正本）を把握する。対象外の機能を同じPRへ混ぜない。
2. 固定mockの該当DOM・文言・操作（`wwwroot`）を読む。**`wwwroot`は変更しない**。製品側の差は `ProductUI/` に置く。

## 実装単位（1接続＝1PR）

1. `src/Sem.Web/Runtime/<対象>Reader.cs`：最小DTO。現在の認証・有効性・権限を同じ読取で再確認する。no-store、query／body／書込の拒否、件数・文字数の上限、別人・欠損は同じ404。DB・監査・cacheを変更しない。
2. `src/Sem.Web/Program.cs`：エンドポイント登録（明示の実認証モードだけ。demoでは接続しない）。
3. `src/Sem.Web/ProductUI/<対象>-view.js`：固定mockのDOMへ描画。取得失敗は偽の空表示・0件にせず再試行を出す。画面遷移・失効・対象相違で旧表示を消し、遅延応答を新しい文脈へ適用しない。HTMLは実行せず文字として表示。必要なら `ProductUI/authentication.js` の読込を更新。
4. `tests/Sem.<対象>.Checks`（`Program.cs`＋csproj）：合成データのメモリ内SQLite。認可・上限・負例を検査。`.github/workflows/main-integration.yml` の検査一覧へ追加。
5. `tests/Sem.Authentication.Fixture/<対象>Fixture.cs`：合成データ。`Program.cs`へ登録。
6. `scripts/<対象>-browser.cjs`：`module.exports=async({page,context,reference,base,check})`。API・表示・失敗／遅延／失効に加え、4幅（320・390・768・1440）×明暗で最終mockとの字体・余白・色の一致を検査。`scripts/product-authentication-browser.cjs` へ接続し、集中実行用の `SEM_FOCUSED_*` を追加。
7. `docs/ui/<対象>-CONNECTION.ja.md`：API・保護・画面・**mockとの意図した差分**・未接続範囲。`IMPLEMENTATION-MAP.ja.md`へ追記。

## 守る境界

- 保存・メール・通知・QR等が未接続なら、成功を偽らずそう表示する。mockの模擬保存を移植しない。
- 全UIの完成・実volume有効化を意味する書き方をしない。
- 検証は `/sem-verify`。台帳（Notion）へ対象版・検証段階・PRを記録する。
