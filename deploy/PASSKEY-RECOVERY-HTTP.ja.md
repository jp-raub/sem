# パスキー復旧HTTP統合

英語正本: `PASSKEY-RECOVERY-HTTP.md`。開発上の契約であり、配備・実メール受信・実機受入の証跡ではありません。この増分の承認時点で運用環境は存在しません。

## 経路と権限

すべて `/api/` 配下です。既存ルーターはGET/POSTを扱い、ハンドラーで `Cache-Control: no-store` を設定します。その他のメソッドはルーティング側405となり、このヘッダーが付くとは限りません。未知の復旧actionは404。`/Account` は有効化しません。

- GET `passkey-recovery-admin-context`: 現行の全体管理者限定。クエリなしで `{actorId}`。単一 `id` で `{actorId,target}`。対象不在はnull。対象はID・表示名・保存済みメールのマスク・有効/復旧状態・操作ID・期限のみ。復旧状態は当該フローによる無効化と現在のstamp/メールの一致を確認します。
- POST `passkey-recovery-admin-options`: `{targetUserId,reason}` → `{publicKey}`。
- POST `passkey-recovery-start`: `{targetUserId,reason,credential,explicitlyConfirmed:true}` → `{operationId,expiresAt,maskedRecipient}`。
- POST `passkey-recovery-resend-options`: `{operationId}` → `{publicKey}`。
- POST `passkey-recovery-resend`: `{operationId,credential}` → 開始結果と同じ形式。初回期限は延長しません。

管理者POSTはCookieの操作者と一致する単一query `expectedActor` が必須です。query値は空・制御文字不可、128文字以内。Origin、query、現行管理者/セッション/stamp、操作者一致をCSRF・本文読取より前に検証します。サービスでも再確認し、ネイティブ証明を対象・理由/操作・管理者・セッションへ結びます。自己リセットは禁止です。

匿名POSTはqueryなしです。

- `passkey-recovery-confirm`: `{operationId,token}` → `{userId,grant,expiresAt}`。
- `passkey-recovery-options`: `{userId,grant}` → `{publicKey}`。
- `passkey-recovery-complete`: `{userId,grant,credential}` → `{message}`。通常ログインCookieは発行せず、登録後に通常のパスキーログインが必要です。

匿名を含めPOSTは同一Originと既存CSRF Cookie/headerが必須です。メールリンクは `/recovery#op=...&token=...`。ブラウザーは通信前にfragmentを除去し、秘密値をJSON本文だけで送ります。token/grant/credentialをquery・監査・診断出力へ残しません。送信先は保存済み確認済みメールだけです。

## 上限と失敗時

CSRF検証より前に受信上限・Content-Length・JSON形式を検証します。空でない単一のX-CSRF-TOKENヘッダーを必須とし、フォーム本文からのCSRFトークン取得は許可しません。

UTF-8 JSONオブジェクトのみ。未知/重複プロパティ、null/型違い、必須欠落は拒否。明示確認は文字列ではなくboolean true。start/resend/complete本文は65,536バイト、その他は4,096バイトまで。Content-Length確認とストリームの上限+1バイト検出を行い、Kestrel側余裕はアプリ上限を増やしません。ID/token/grantは128文字、理由500文字、credential文字列50,000文字以内。サービス側で理由をさらに正規化・検証します。

400は入力/CSRF/証明、403は権限・無効/期限切れgrant、409は更新競合、413は本文超過、429は永続的な送信/失敗制限（Retry-After）です。不明なPOST結果を自動再送しません。管理者は読取専用contextで状態を再確認します。匿名登録完了の結果不明時もcredentialを自動再送せず、新しい復旧または通常ログインで確認します。

限定grantは通常セッションではありません。ネイティブキー保存とgrant消費が同時に確定するまで対象は無効のままです。復旧テーブル・メール保護purpose・制限・監査はメール変更と別です。

## 検証の区別

専用HTTP試験は使い捨ての移行済みSQLiteとloopback Kestrel、試験DIだけの合成証明を使います。Core試験はサービス/移行/送信処理を検証します。累積CIは両.NET試験を自動検出しUIのNodeチェックも実行します。合成試験と、production adapter＋仮想認証器を使うローカルHTTPSブラウザー試験、物理端末の受入は区別して報告します。outbox検証は実メール受信を証明しません。

## 現行セッションの失効

HTTP共通ミドルウェアは認証済み要求ごとに有効／確認済み状態・SecurityStamp・セッション識別子・失効状態を確認します。無効な通常Cookieはサインアウトし、要求のprincipalを消去します。Stamp更新により、復旧後に再有効化しても旧Cookieを拒否します。Blazorの表示上の認証状態は30秒ごとの再検証であり、即時Circuit切断ではありません。Coreの各操作も現行ユーザー／Stamp／セッションを再確認するため、この表示更新周期を認可の猶予とは扱いません。旧SEM.Enrollmentのオプション・登録完了は復旧無効化状態を拒否し、完了トランザクション内でも再確認します。
