# パスキー認証の復旧UI

この変更はブラウザーUIのみを提供します。ルーティング、認可、challenge検証、状態遷移、永続化、メール送信、失効処理はバックエンドの責務です。UI側の検証は多層防御であり、サーバー検証の代替ではありません。

## 管理者による復旧

管理者ページは `/service/passkey-recovery` です。コントロールは初期状態で非表示にし、ブラウザーのイベントリスナーを設定してから `GET /api/passkey-recovery-admin-context` を呼びます。応答は `{ "actorId": "..." }` です。対象を明示入力して `GET /api/passkey-recovery-admin-context?id=<targetUserId>` を呼ぶと、`{ "actorId": "...", "target": { "id": "...", "displayName": "...", "maskedRecipient": "...", "enabled": true, "recovering": false, "operationId": "...", "expiresAt": "..." } }` が返ります。復旧処理がない場合、operationと期限の項目は省略可能です。actor IDと対象IDは制御文字なし・128文字以内とし、伏字でないメールらしい値は拒否して、サーバーが返した伏字だけを表示します。

新規開始には対象の明示入力、前後の空白を除いて3〜500文字の理由（制御文字なし）、既存の全パスキーとセッションが無効になり保存済みメールだけに送られることへの画面上の明示確認が必要です。期限切れ後の新規開始にも、新しい理由を入力します。`POST /api/passkey-recovery-admin-options?expectedActor=<actorId>` に `{ "targetUserId": "...", "reason": "..." }` を送り、応答 `{ "publicKey": <native request options> }` を受け取ります。ブラウザーで `navigator.credentials.get` を実行し、`POST /api/passkey-recovery-start?expectedActor=<actorId>` に `{ "targetUserId": "...", "reason": "...", "credential": <serialized assertion>, "explicitlyConfirmed": true }` を送信します。

進行中の処理は、期限が未来で対象が無効かつ復旧中の場合のみ再送できます。`POST /api/passkey-recovery-resend-options?expectedActor=<actorId>` に `{ "operationId": "..." }` を送り、`navigator.credentials.get` による本人確認後、`POST /api/passkey-recovery-resend?expectedActor=<actorId>` に `{ "operationId": "...", "credential": <serialized assertion> }` を送ります。再送では初回に記録された理由を保持し、新しい理由を要求・送信しません。無効化済み対象でも、サーバーが期限切れの既存処理を返した場合は、新しい理由と確認を取り直して新規復旧を開始できます。開始・再送の成功表示は「送信キュー登録」であり、配達済みとは表現しません。

確認チェックは開始・再送の成功時と進行中処理の期限切れ時に解除します。成功時および対象変更時には入力理由も消去し、期限切れ後の再開始には新しい理由とチェック操作を必要とします。

認証済みPOSTの `expectedActor` クエリは必ず1つだけです。actor、選択対象、新規開始時の理由、確認状態、画面世代を証明フロー中固定します。変更時は証明を無効化し、書き込み前に対象状態と処理期限を再確認します。開始・再送はネイティブのパスキーassertionを用い、ログインには使用しません。POSTは自動再送しません。結果不明のPOST後は、読み取り専用の明示的な状態再取得で整合するまで書き込みをロックします。`401`/`403`ではactor状態を消去し、`Retry-After`を待機案内として示します。

## 公開復旧と新しいパスキー

匿名ページは `/recovery#op=<operationId>&token=<secret>` です。フラグメントだけを読み取り、ネットワーク要求の前に `history.replaceState` でURLから除去します。確認はクエリ文字列なしで `POST /api/passkey-recovery-confirm` に `{ "operationId": "...", "token": "..." }` を送り、`{ "userId": "...", "grant": "...", "expiresAt": "..." }` を受け取ります。この確認で通常の認証済みセッションは作成しません。

user IDとgrantはJavaScriptメモリー内だけに置きます。grantはサーバー期限または受信後5分のうち早い方で消去し、期限切れまたは画面・ナビゲーション終了時は登録操作を無効化します。登録は明示的なボタン操作で開始します。`POST /api/passkey-recovery-options` に `{ "userId": "...", "grant": "..." }` を送り、`{ "publicKey": <native creation options> }` を受け取ってから、ブラウザーで `navigator.credentials.create` を実行します。その後 `{ "userId": "...", "grant": "...", "credential": <serialized attestation> }` を `POST /api/passkey-recovery-complete` に送り、`{ "message": "..." }` を期待します。UIからセッションは発行せず、成功後は通常のログイン画面 `/` へ案内します。`/Account`には遷移しません。

## 通信とバックエンドの責務

すべてのPOSTの前に `GET /api/csrf` でCSRF情報を取得し、`X-CSRF-TOKEN` を送ります。通信には `credentials: same-origin`、`mode: same-origin`、`cache: no-store`、`redirect: error` を使い、公開POSTにクエリ文字列は付けません。要求は25秒、ネイティブ認証操作は最大5分で制限します。JSON応答は1 MiB、CSRF応答は64 KiBまでとし、UTF-8は厳格にデコードします。UI表示は `textContent` を使い、エラーをHTMLとして解釈しません。秘密情報、grant、assertion、tokenをlocal/session storageやログに保存しません。

バックエンドはcookieに基づく管理者認可、actor照合、CSRFとOrigin検証、厳格なリクエスト解析、WebAuthn challenge/RP/origin/署名/attestation検証、対象資格と期限、一度限りのtoken/grant、保存済みアドレスだけへの送信、原子的処理、既存キーとセッションの失効を独立して強制します。UIはこれらの保証を作成・緩和しません。
