# 招待一覧・失効 API

この API は安全な招待／依頼一覧と、理由を記録する管理者専用の失効だけを追加します。招待の発行、依頼の作成・判断、トークン消費、登録処理は変更しません。DB スキーマ変更もありません。

## `GET /api/core/invitations`

省略可能なクエリは `page` のみで、1～10000 の整数です。省略時は1ページ目です。招待と依頼はそれぞれ固定50件で、`hasMore` 判定のために独立して51件を取得します。順序は `Created` の降順、続いて `Id` の昇順です。応答は `Cache-Control: no-store` です。

応答形式：

```json
{
  "actorId": "…",
  "isAdmin": true,
  "page": 1,
  "pageSize": 50,
  "hasMoreInvitations": false,
  "hasMoreRequests": false,
  "invitations": [],
  "requests": []
}
```

招待行には `id`、`ownerId`、`ownerName`、`issuerId`、`issuerName`、`requestId`、`limit`、`uses`、`created`、`expires`、`disabled`、`revokedAt`、`revokedBy`、`revokerName`、`revocationReason` のみを含みます。依頼行には `id`、`userId`、`userName`、`reason`、`count`、`days`、`status`、`created`、`decidedAt`、`decidedBy`、`deciderName` のみを含みます。時刻は既存のUnix秒の数値または `null` です。表示名が未登録・空の場合は `不明` とします。

管理者は全行を参照できます。非管理者は自分が所有する招待と、自分が作成した依頼だけを参照できます。トークンハッシュ、保護済みトークン、招待リンク、メール値、過去の目的・発行元の推測情報は返しません。

## `POST /api/core/invitations/revoke?expectedActor=<actorId>`

本文は次のプロパティだけを一度ずつ含めます。

```json
{"id":"…","reason":"…","explicitlyConfirmed":true}
```

有効な管理者のみ実行できます。`expectedActor` は認証済みactorと完全一致し、設定済みOriginとの完全一致と、空でない `X-CSRF-TOKEN` ヘッダー1つが必要です。Content-Lengthの上限を antiforgery 検証前に適用し、長さ不明のストリームも4KiBまでに制限します。JSONはUTF-8エラーを許容せず、浅い構造で型検証し、フォーム代替は受け付けません。

失効理由はトリム後1～500文字で、制御文字を含められません。IDは1～128文字で、制御文字を含められません。サービス側でもライブユーザー、Enabled／EmailConfirmed、SecurityStamp、セッション、管理者権限、明示確認を再検証します。成功時はサーバー由来の値で `disabled`、`revokedBy`、`revokedAt`、`revocationReason` を設定し、招待IDを含む `InvitationRevoked` 監査を記録します。更新と監査は同一トランザクションです。

既に無効化済みの場合は冪等に成功し、元の理由・実行者・時刻を保持して監査を追加しません。既存の登録時消費処理と `Uses` の計上は変更しません。

既知のルートは一般的なHTTPメソッドを明示的に受け付け、未対応メソッドには `Allow` 付き405、未知のサブアクションには404を返します。全応答に `Cache-Control: no-store` を設定します。
