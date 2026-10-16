# 管理者による招待発行

`POST /api/core/invitations/issue?expectedActor=<current-user-id>` は、既存の旧招待発行・依頼承認ルートを変更せず、目的付きの直接招待を追加します。

`application/json`、設定済みOriginとの完全一致、空でない `X-CSRF-TOKEN` ヘッダー1つ、現在有効な管理者セッション、`expectedActor` の完全一致が必要です。本文はAntiforgery検証前および長さ不明本文の読取時にも4KiB以内とします。厳密なUTF-8 JSONで、次の項目と型だけを受け付けます。

```json
{"purpose":"プロジェクト参加","count":3,"days":7}
```

目的は前後空白を除去し、制御文字なしの1～500文字とします。`count` は既存ポリシーどおり1～5、`days` は1～7です。

成功応答:

```json
{"actorId":"…","id":"…","purpose":"プロジェクト参加","issuanceSource":"Direct","limit":3,"uses":0,"created":0,"expires":0,"link":"https://example.invalid/join#invite=…"}
```

URLは設定済み公開オリジンからサーバー側で生成し、常に `/join#invite=…` を使います。招待tokenはqueryではなくフラグメントに置きます。応答は `Cache-Control: no-store` です。生token／リンクは監査記録や招待一覧DTOに含めず、DBには既存どおりtoken hashと保護済みtokenを保存します。

招待行と `InvitationIssued` 監査行は同一トランザクションで確定します。OwnerとIssuerは現在の管理者、`RequestId` はnull、`Uses` は0で、有効期限は既存の1～7日ポリシーを使います。登録消費は変更せず、native passkey完了時に招待を再確認して `Uses` を増やします。

`Purpose` と `IssuanceSource` はnullable列です。既存行の値はnullのまま保持し、`RequestId` や監査履歴から目的・発行元を推測しません。既存の旧発行ルートと依頼承認ルートも変更せず、これらの値を設定しません。