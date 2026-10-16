# 招待直接発行UI

## 対象範囲

招待の直接発行を、認証必須の既存 `/invitations` 管理画面へ追加します。発行UIはAPIが返すactorが管理者と確認できた後だけ有効になります。権限、発行上限、有効期限、目的の最終検証と発行判断はバックエンドが担います。

依頼の作成・承認、依頼に基づく発行、自動リンク配布はこの範囲に含みません。一覧のページングと既存招待の管理者失効は従来どおりです。詳細は [招待管理UI](INVITATION-MANAGEMENT-UI.ja.md) を参照してください。

## 発行要求

管理者が明示確認欄をチェックした場合だけ、次の要求を送ります。

`POST /api/core/invitations/issue?expectedActor=<actorIdと完全一致>`

`GET /api/csrf` から最新CSRFトークンを取得し、`X-CSRF-TOKEN` ヘッダーに設定します。JSON本文は次の3フィールドだけです。

```json
{"purpose":"…","count":1,"days":1}
```

目的は前後空白を除き1〜500文字、C0/C1制御文字なしとします。利用上限は1〜5の整数、有効日数は1〜7の整数で、どちらも初期値は1です。目的・上限・日数を変更すると確認欄と以前表示したリンクを消去します。要求前に値とactorをsnapshotし、古いowner/root/generationの結果は適用しません。

成功応答は `actorId`、`id`、`purpose`、`issuanceSource`、`limit`、`uses`、`created`、`expires`、`link` の9フィールドちょうどでなければなりません。actor、ID、正規化済み目的、`issuanceSource: "Direct"`、要求上限、使用数0、Unix秒timestamp、same-originの `/join#invite=…` リンク（クエリ文字列なし）を検証します。不正・上限超過応答は発行成功とみなさず、結果不明として扱います。

## 一時的な秘密リンク

応答全体を検証した後、秘密リンクはページメモリに保持し、一時的な読み取り専用欄だけに表示します。アンカー/list行としてrenderせず、自動コピーせず、ブラウザー保存領域・ログ・現在ページのquery/fragment/historyへ書き込みません。管理者が明示的に「リンクをコピー」を押したときだけclipboardへ渡します。clipboardを利用できない場合は手動コピー用に欄を選択します。再読み込み、actor変更、root破棄、および同期 `pagehide` handlerでリンクを消去します。`pagehide` は表示中のactor/listと確認状態も消去し、進行中UI操作を無効化して通信をabortします。BFCacheからの `pageshow` ではリンクや確認状態を復元せず、書き込みを再有効化する前にpage 1のactor/listを読み取り専用で再取得します。古いPOSTやclipboard完了は復帰後UIへ反映しません。明示操作でブラウザーAPIへ渡したclipboard書き込み自体は取り消せませんが、遅れて返る完了結果は無視します。表示値は永続記録ではありません。招待レコード自体に秘密リンクを保存しません。

一覧はAPIのnullable `purpose` と `issuanceSource` を表示します。nullまたは空値は「未記録」とし、過去値を推測しません。一覧行には招待tokenやリンクを表示しません。

## 結果不明時と制約

発行APIにはidempotency保証がありません。発行POSTを開始した可能性がある後にtimeout、通信断、不正・上限超過応答などが起きた場合、自動再送は禁止です。表示リンクを消去し、現在のページ状態で管理者変更操作をロックし、明示的な読み取り専用一覧再読込だけを残します。再読込一覧には応答で得られなかったIDがないため、発行の成否を確実には照合できません。したがってロックは維持し、再試行可能とは表示しません。BFCacheのhide/restoreでは結果不明ロックを維持し、書き込みは行いません。完全なdocument再読み込みではページメモリの状態が消えるため、再発行前に管理者が一覧等で手動照合する必要があります。401/403ではactorに紐づく状態と一時リンクを破棄し、再送しません。

要求にはsame-origin credentials/mode、`redirect: "error"`、`cache: "no-store"`、bounded timeout、fatal UTF-8 JSON解析、バイト上限を適用します。CSRF/Origin、actor一致、権限、要求値のサーバー側検証はバックエンドの責務です。APIやユーザー由来の文字列は `textContent` で表示し、raw HTMLとして扱いません。

## 安定UIフック

- `invitation-issue`
- `invitation-issue-heading`
- `invitation-issue-form`
- `invitation-issue-fields`
- `invitation-issue-purpose`
- `invitation-issue-count`
- `invitation-issue-days`
- `invitation-issue-confirm`
- `invitation-issue-submit`
- `invitation-issue-result`
- `invitation-issue-result-heading`
- `invitation-issue-link`（一時的な読み取り専用秘密リンク欄）
- `invitation-issue-copy`
- `invitation-issue-status`
- `invitation-issue-error`
- `invitation-issue-copy-status`

## チェック

合成UIチェックには発行値・確認の検証、要求bodyとactor binding、一時リンクの表示・コピー動作、root/actor変更時の消去、結果不明後の読み取り専用照合と自動再送なし、および `pagehide`/BFCache `pageshow` での秘密情報消去、遅延POST/clipboard結果の無視、listener破棄、読み取り専用復帰を含みます。親統合側で以下を実行してください。

```sh
node --check src/Sem.Web/wwwroot/invitation-management.js
node --check scripts/invitation-management-ui-checks.mjs
node scripts/invitation-management-ui-checks.mjs
```

このテストはendpoint/CSRFテスト、.NET build、host/browser受入試験の代替にはなりません。