# メールアドレス変更UI

ログイン済みメンバーが新しいメールアドレスを申請し、既存アカウントのパスキーで本人確認した後、専用のメール確認リンクから変更を確定するUIです。この変更はWeb UIだけを対象とし、バックエンドは以下のAPI契約を提供する必要があります。このUI自体はSMTP設定を変更せず、実メールも送信しません。

## プロフィール画面のフロー

- プロフィール設定画面は `GET /api/email-change-status` を呼び、`{ "actorId": "...", "pending": null | { "operationId": "...", "expiresAt": "<ISO-8601>", "lastSentAt": null | "<ISO-8601>", "confirmationSent": true | false } }` を想定します。`actorId` はリクエスト照合のためメモリー内に保持しますが、画面には表示しません。保留中のメールアドレスも表示しません。
- 申請開始時は `POST /api/email-change-begin?expectedActor=<actorId>` に `{ "newEmail": "..." }` を送り、`{ "operationId": "...", "publicKey": <WebAuthnの認証リクエストオプション> }` を受け取ります。既存のWebAuthnオプション処理と認証情報のシリアライズを使い、パスキー証明後に `POST /api/email-change-reauth?expectedActor=<actorId>` へ `{ "operationId": "...", "credential": "<シリアライズ済み認証情報JSON>" }` を送ります。
- 再送時は毎回 `POST /api/email-change-reauth-options?expectedActor=<actorId>` に `{ "operationId": "..." }` を送り、新しいオプション `{ "operationId": "...", "publicKey": <WebAuthnの認証リクエストオプション> }` を取得します。パスキーによる再認証が完了した後でのみ、`POST /api/email-change-reauth?expectedActor=<actorId>` で確認メールを送信・再送します。
- 取り消しでは `POST /api/email-change-cancel?expectedActor=<actorId>` に `{ "operationId": "..." }` を送ります。認証が必要な全POSTには `expectedActor` をちょうど1つ付けます。バックエンドはCSRF確認や本文読み取りより前に、現在有効なアクターと照合します。成功応答には上限付きの `message` 文字列が含まれますが、UIはサーバーのHTMLを描画せず固定の状態メッセージを表示します。
- 状況取得APIから保留中アドレスを取得せず、画面にも表示しません。開始応答を受け取った後にメールアドレス欄を消去し、アドレス・トークン・認証情報・操作状態をlocalStorageへ保存しません。
- 状況取得が成功して現在のアクターを確立するまでは、認証を伴う操作をロックします。アクターが変わると古いリクエスト／画面世代を無効化し、`401`／`403` ではアクターと保留状態を消去して、明示的な状況再取得が必要になります。
- POST結果が不明でも自動再送しません。読み取り専用の状況GETで照合できますが、成功するまでは操作をロックします。`429`／`Retry-After` の場合は待機後に状況を再取得し、保留があれば再認証、なければ最初から申請するよう案内します。

## 公開確認リンク

公開ルートは `/profile/email-change-confirm#op=<operationId>&token=<secret>` です。両値はフラグメントからのみ読み取り、トークンをクエリ文字列やパスからは受け付けません。スクリプトはCSRF取得やPOSTより前に `history.replaceState` でフラグメント（およびクエリ）をただちにブラウザー履歴から削除します。その後、匿名・クエリなしの `POST /api/email-change-confirm` のJSON本文 `{ "operationId": "...", "token": "..." }` でのみ値を送信します。このAPIに `expectedActor` は付けません。

確認APIは通常のログインセッションを要求せず、メールアドレス変更専用トークンだけで該当操作を確定し、通常のサインイン／セッションを発行してはなりません。UIも確認後に認証済みセッションを作成したり、その画面へ遷移させたりしません。トークンはログ出力・画面表示・ブラウザー保存を行いません。POSTは同一オリジンfetch、CSRFトークン、キャッシュ無効化を使い、リダイレクトを拒否します。

## 通信とプライバシー

- 全POSTの前に `GET /api/csrf` でトークンを取得し、`X-CSRF-TOKEN` を付けます。`credentials: same-origin`、`mode: same-origin`、`redirect: error`、`cache: no-store` を指定し、同一オリジンPOSTの `Origin` ヘッダーはブラウザーに付与させます。
- JSON応答と送信JSON本文にはサイズ上限を設け、リクエストは25秒で打ち切ります。サーバーのエラー本文は読み取らず、画面にも表示しません。
- 状況APIのサーバー側スコープは現在のアクターです。クライアントは `actorId` を認証POSTの照合にのみメモリー内で使用し、保留中アドレスは描画しません。
- アカウント所有者の確認、CSRF／Origin検証、厳格なJSON解析、トークン検証・消費、メール送信、およびメールアドレス変更のトランザクション処理はバックエンドの責務であり、このUIファイルでは実装しません。