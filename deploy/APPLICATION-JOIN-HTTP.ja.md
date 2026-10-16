# 本体向け招待登録とメール確認

本体専用の匿名join APIを追加し、既存の招待・仮ユーザー・`EmailTicket`・Identity標準native passkey完了処理を再利用します。外部テストHome、`/api/register`、`/verify`、passkey options／complete、および既存の招待消費タイミングは変更しません。DBスキーマ・新しいクォータ・チケット用途列は追加しません。

## `POST /api/join/register`

クエリなしのJSON本文に、`invite`、`name`、`email` を各1つだけ指定します。

```json
{"invite":"…","name":"…","email":"…"}
```

匿名利用者のみ受け付けます。設定済みOriginとの完全一致、空でない `X-CSRF-TOKEN` ヘッダー1つ、`application/json` が必要です。Antiforgery検証前と長さ不明本文の読取時の両方で上限を4KiBにします。JSONは厳密なUTF-8、浅い深さのオブジェクトとし、重複／未知項目を拒否し、各値は文字列だけ受け付けます。未対応メソッドは `Allow: POST` 付き405、クエリパラメーターは拒否します。

サービスは既存の登録規則を適用します。メール配信設定があり、サイト初期化済みであること、招待が有効・未期限切れ・残数ありであること、仮ユーザー再開時は同じ招待であることを確認します。1時間有効の `EmailTicket` をハッシュで保存し、同じ仮ユーザーの以前の未使用チケットを無効化します。確認メールは登録済みアドレスに、既存の保護付き配信キュー経由で登録します。応答は `{ "message": "確認メールを送信待ちに登録しました。到着後、リンクを開いてパスキーを登録してください。" }` です。

新しいメールリンクは固定の同一オリジン `/join#token=<token>` です。tokenはURLフラグメントに置かれ、最初のページリクエストではサーバーへ送られません。Join UIはユーザーの明示操作後にconfirm APIへPOSTし、ページ表示だけで確認を完了させてはいけません。

## `POST /api/join/confirm`

クエリなしのJSON本文に、文字列の `token` を1つだけ指定します。

```json
{"token":"…"}
```

registerと同じ匿名・Origin・CSRF・4KiB本文・UTF-8・JSON・メソッド条件を適用します。有効な未使用チケットを原子的に使用済みにし、メールを確認済みにします。既存の非永続 `SEM.Enrollment` Cookie（有効期限20分）を発行し、`{ "message": "メール確認が完了しました。この画面で期限内にパスキーを登録してください。" }` を返します。Join UIはその後 `/api/status` でEnrollment状態を確認できます。消費済み・期限切れ・無効なチケットは拒否します。既存チケットも受け付けます。チケット用途は永続化しません。

## 既存フローとの互換性と完了境界

既存の `POST /api/register` は同じ登録サービスを使い、従来の応答を維持します。`GET /verify?token=...` は従来どおり同じ一回限りのチケットを消費してEnrollment Cookieを発行し、`/?enroll=1` へリダイレクトします。既存の `/?invite=...` リンクと外部テストHomeは変更しません。`/join` は独立した本体向け画面です。

メール確認後のnative登録は、既存の `POST /api/passkey-options` と `POST /api/passkey-complete` を使います。後者はnative attestationとユーザー一致を検証し、既存トランザクション内で招待を再検証して `Uses` を増やし、ユーザーを有効化してパスキーを保存します。完了成功時にのみEnrollment Cookieを消し、通常のアプリケーションCookieを発行します。この処理とAPIは変更しません。

本スコープでは実メール配信や実ユーザー登録を実施しません。
