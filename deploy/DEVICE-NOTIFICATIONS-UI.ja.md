# 現在のブラウザー向けPush通知設定

## 対象範囲

`/notifications` は、現在ブラウザーの登録とイベント別の本人開始通知を同じ入口で扱います。Push登録欄はログイン中の現在のブラウザーだけを管理します。`/` の外部・診断用画面とは分離します。他の端末の一覧・編集、テスト通知、メール配信は行いません。イベント設定欄は既存の本人用設定を再利用します。詳しくは [UI優先実装の契約](UI-FIRST-IMPLEMENTATION.ja.md) を参照してください。

ページは静的プリレンダリングを無効にし、Interactiveコンポーネント開始後に `wwwroot/device-notifications.js` を読み込みます。既存のルートスコープ `/sem-sw.js` を使用します。ナビゲーションリンクはドキュメント全体を読み直す境界とし、古い静的スクリプト状態で未初期化になることを防ぎます。

## 読み取り専用の初期化

ページ表示時にブラウザー機能と `Notification.permission` を読み、先に `GET /api/core/device-notifications` から現在の `actorId`、`pushConfigured`、`pushPublicKey` を取得します。公開鍵は、P-256曲線上にあるprefix `0x04` の65-byte非圧縮点を表す正規のpaddingなしbase64urlでなければなりません。不正な鍵なら許可ダイアログを出す前に初期化を停止します。その後 `getRegistration('/')` と `getSubscription()` を呼び出します。表示時に `Notification.requestPermission()`、Service Worker登録、購読作成、既存購読の再紐付けは行いません。ローカル購読がある場合は `GET /api/csrf` でCSRFトークンを取得し、読み取り専用の状態確認を行います。

- `POST /api/core/device-notifications/status?expectedActor=<contextと同一のactorId>`
- JSON本文：`{ "endpoint": "..." }`
- 期待する応答：`{ "actorId": "...", "registered": true|false }`

`false` でも自動登録しません。このブラウザーに購読がない場合、現在のendpointをAPIへ提示できないため、サーバー側の状態をこのブラウザーから確認できないことを画面に明示します。

すべてのAPI要求はsame-originのcredentials/mode、`redirect: "error"`、`cache: "no-store"`、25秒の中断上限を使用し、fatal UTF-8で最大16 KiBのJSONだけを読み取ります。変更要求にはCSRFトークンを `X-CSRF-TOKEN` で渡し、contextで取得したactorを `expectedActor` クエリへ一度だけ含めます。endpoint、購読鍵、CSRFトークン、応答中の秘密値は画面文字列・属性・ブラウザー保存領域・ログへ出力しません。

## 明示操作

通知許可の要求、`/sem-sw.js` のscope `/` への登録、ブラウザー購読の作成または再利用、サーバーへの登録は「このブラウザーでPushを登録」操作に限ります。

- `POST /api/core/device-notifications/register?expectedActor=<contextと同一のactorId>`
- JSON本文：`{ "endpoint": "...", "p256dh": "...", "auth": "..." }`
- 期待する応答：`{ "actorId": "...", "registered": true }`

サーバー紐付けとローカル購読の解除は「このブラウザーのPush登録を解除」操作に限ります。

- `POST /api/core/device-notifications/unregister?expectedActor=<contextと同一のactorId>`
- JSON本文：`{ "endpoint": "..." }`
- 期待する応答：`{ "actorId": "...", "registered": false }`

解除はサーバー側を先に行います。サーバーが紐付け解除を確認した後にだけ `PushSubscription.unsubscribe()` を呼びます。unsubscribeと続く`getSubscription()`が両方失敗した場合は、サーバー解除済み・このブラウザーの購読状態は未確認・手動で再確認が必要、と表示し、登録／解除操作を無効にします。これはサーバー書込み結果不明とは区別し、自動再送しません。購読を解除してもブラウザー／OSの通知許可は取り消されません。ログアウト時はサーバー側のセッション紐付けが解除されます。再ログイン後、利用するブラウザーで明示的に再登録してください。

変更応答の不正・欠落、タイムアウト、通信断は結果不明として扱います。追加の変更操作をロックし、自動再送しません。利用者が状態再確認を明示実行してください。再確認ではactorとローカル購読を読み直し、有効な状態応答を得た場合にだけロックを解除します。ローカル購読がない場合はこのAPIでサーバー状態を照合できないため、ロックを維持します。

ブラウザーのネイティブ変更処理（`serviceWorker.register()`、`PushManager.subscribe()`、`PushSubscription.unsubscribe()`）はJavaScriptのタイムアウトではキャンセルできません。25秒の待機上限を超えても元のブラウザー処理が保留中なら、登録／解除をロックしたままにします。処理中の手動再確認は読み取り専用であり、ロックを解除しません。元のPromiseが完了した後、利用者がもう一度明示的に再確認し、最新のローカル状態（購読があればサーバー状態も）を読み直した場合にだけロックを解除します。ネイティブ処理・サーバー処理を自動再実行しません。同じJavaScriptモジュールが動作している間はowner/rootが切り替わっても保留状態を引き継ぎ、遅れて完了した処理が後続画面の操作を誤って有効にしないようにします。ページ全体を再読み込みすると新しいJavaScriptモジュールになり、旧Promiseの追跡状態は引き継げません。再読み込みや別の変更操作の前に実行中のブラウザー処理が終わるのを待ち、その後に状態を手動で確認してください。

## アクセシビリティとライフサイクル

認証必須・interactive-onlyの画面です。操作ボタンは初期状態で無効です。JSモジュール読み込み失敗は `role="alert"` で示し、操作は有効化しません。状態・許可メッセージは読み上げ可能なステータス要素に表示します。表示はすべて `textContent` を使い、API文字列や秘密値をHTMLへ挿入しません。モジュールはowner/rootごとに初期化し、破棄時にイベントリスナーを解除し、要求を中断し、JavaScript側の待機を終了します（元のブラウザーネイティブ処理そのものはキャンセルできません）。世代を更新してactor・鍵・購読・registration参照を破棄します。ネイティブ変更処理はPromiseが完了するまで追跡し、古い非同期処理が旧画面を書き換えず、後続ownerも手動照合までロックされるようにします。

## 意図的に対象外とすること

テスト通知ボタンはありません。既存のテスト送信はアカウント全体へ影響し、現在のブラウザーだけの操作として扱えないためです。合成Nodeテストではfake timerと遅延native Promiseを使い、`ServiceWorker.register()`、subscribe、unsubscribeそれぞれのタイムアウト、ネイティブ処理中の読み取り専用再確認、root/owner切替、遅延完了、完了後の手動照合と自動変更なしを検証します。実ブラウザーの許可ダイアログ、実際のService Worker登録、Push購読、配信到達性は検証しません。HTTPSの実ブラウザー／端末で、明示許可、登録、状態確認、解除順序、ログアウト後の再登録、実際の配信を承認済み手順で別途受入確認してください。
