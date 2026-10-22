# 現在の端末向け Push 通知 API

この機能は、認証済みブラウザーに紐づく Push 購読だけを管理します。テスト Push の送信や Push プロバイダーへの接続は行いません。DB スキーマは変更せず、既存の `PushDevices` 行を現在の SecurityStamp と認証セッションのハッシュに紐付けます。

すべての応答に `Cache-Control: no-store` を設定します。

## `GET /api/core/device-notifications`

クエリパラメーターは受け付けません。有効で、Enabled かつメール確認済みのライブセッションが必要です。Push が設定済みの場合だけ、公開 VAPID 鍵を返します。

```json
{"actorId":"…","pushConfigured":true,"pushPublicKey":"…"}
```

秘密鍵や保存済み endpoint 資格情報は返しません。

## `POST /api/core/device-notifications/status?expectedActor=<actorId>`

本文：`{"endpoint":"https://…"}`。応答は `{"actorId":"…","registered":true|false}` です。`registered` が true になるのは、保存済み endpoint が現在の利用者、SecurityStamp、認証中のブラウザーセッションにすべて紐付く場合だけです。

## `POST /api/core/device-notifications/register?expectedActor=<actorId>`

本文：

```json
{"endpoint":"https://…","p256dh":"…","auth":"…"}
```

`endpoint` は HTTPS で、既存の Push サービス許可リストに含まれる必要があります。`p256dh` は非圧縮 P-256 公開点65バイトを表す、正規化済み・パディングなし base64url とします。P-256 曲線上の有効点としてインポート・検証し、曲線外の点を拒否します。`auth` は同形式の16バイトです。登録には Push 設定が必要で、行を現在の SecurityStamp とセッションハッシュに紐付けます。Push 送信は行いません。

既存 endpoint の利用者、stamp、session の紐付けを変更する場合は、現在保存されている `p256dh` と `auth` の両方を完全一致で提示する必要があります。比較は固定時間で行います。鍵情報が不足または不一致なら、既存行を変更せず拒否します。したがって、明示的な再紐付けには実際の購読秘密鍵が必要で、endpoint URL だけでは所有権を移せません。

## `POST /api/core/device-notifications/unregister?expectedActor=<actorId>`

本文：`{"endpoint":"https://…"}`。現在の利用者、SecurityStamp、認証セッションハッシュがすべて一致する場合だけ削除します。別利用者や同一利用者の別セッションからは削除できません。該当行がない場合も冪等に `registered:false` を返します。

## リクエスト保護

操作ルートは POST のみで、未対応メソッドには `Allow: POST` 付き405を返します。ルートは GET のみで、未対応メソッドには `Allow: GET` 付き405を返します。未知の操作は404です。操作リクエストには、ライブ利用者と一致する `expectedActor` 1つ、設定済み Origin との完全一致、空でない `X-CSRF-TOKEN` ヘッダー1つ、JSON形式が必要です。フォーム代替は受け付けません。本文は antiforgery 処理前および長さ不明ストリームの読み込み時に16KiBまでに制限します。UTF-8を厳密に検証し、JSONの深さを制限し、許可された項目だけを重複なく文字列型で受け付けます。

`Sem.DeviceNotificationChecks` は合成データで HTTP／サービス保護を検査するプロジェクトです。リクエスト検証、サイズ制限、ライブ利用者／stamp／session、接続先・鍵検証、曲線上の P-256 点、鍵一致を伴う同一利用者・他利用者への再紐付け、鍵が一致しない移管拒否、別セッション解除の拒否、所有者による解除を対象とします。Push プロバイダーには接続しません。

## 旧購読URLの互換境界

`POST /api/subscribe` と `POST /api/unsubscribe` は旧 `sem.js` のために維持しますが、独立したDB更新処理は廃止し、上記と同じHTTP検証・`DeviceNotificationService` へ委譲します。正確なURLルートが旧 `/api/{**action}` より優先されます。GET等は405です。

旧登録本文は `endpoint`・`p256dh`・`auth`、旧解除本文は `endpoint` のみです。旧クライアントは `expectedActor` を送らないため、これら2つの互換URLだけはクエリを一切受け付けず、リクエストのライブ利用者を対象にします。現行URLの `expectedActor` 必須条件は緩めません。登録先の所有者／stamp／sessionを変更するには保存済みの両鍵の一致が必要で、解除は現在の利用者・stamp・sessionが一致する行に限定します。旧画面でアカウントを切り替えた場合も、元の利用者の購読をendpointだけで移管・解除できません。

両互換URLにも、正確なOrigin、CSRFヘッダー、厳格なJSON／UTF-8、16KiB制限、有効なP-256点と接続先の検証を適用します。成功応答には従来の `message` を残し、`actorId` と `registered` も返します。DB更新競合は内容を公開しない409応答です。

この変更は購読管理の保護統一であり、テスト通知ボタン、全端末管理、実Push送信を追加するものではありません。DB migration、CD方式、実機への通知到着を変更・実証するものでもありません。実際の被害や旧経路による障害が発生していたという判断は行いません。
