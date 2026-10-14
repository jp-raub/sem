# イベント取込 HTTP 境界

イベント取込のHTTP境界は、既存のプレビュー／適用サービスのみを公開します。サンプル取得ルート、UI、外部URL取得、本番設定、データベースマイグレーションは追加しません。

## ルート

既存の `/api/core/{action}` マップ内で、いずれも POST 専用です。GET は `405 Method Not Allowed` を返します。

- `POST /api/core/event-import-preview?id=<event-id>` — 本文: `{"csv":"..."}`
- `POST /api/core/event-import-apply?id=<event-id>` — 必須本文フィールド: `requestId`、`rowVersion`、`previewFingerprint`、`csv`、`confirmed`、`confirmOverrun`

`id` クエリパラメーターは1個だけ指定し、空であってはなりません。クエリ／本文のactor IDを読み取ったり信頼したりしません。既存の認証済みCookie principalをそのままCoreサービスへ渡します。プレビューは読み取り専用であり、適用時の権限確認、版の競合検出、受付票の作成、再送応答は既存Coreサービスに委譲します。

## リクエスト保護と上限

- 最初にOriginを確認し、イベントIDの検証後、読み取り専用の軽量な事前認可で現在のCookie principal、セッション／security stamp、イベント管理権限を確認します。antiforgery検証や本文処理より前に実行します。プレビュー／適用はTOCTOU対策として各Coreトランザクション内でも認可を再確認します。事前認可では取込履歴・フェーズ適格性を追加検証しないため、権限のある管理者はフェーズ変更後も既存受付票を再取得できます。
- JSONは厳密に検証します。宣言済みフィールドはすべて必須で、未知のフィールドは拒否します。プレビュー本文は `csv` のみ、適用本文は既存の厳密な `EventImportApply` モデルを使います。
- CSVパーサーの上限はUTF-8 CSV 1 MiBのままです。JSONエスケープ後のCSVと適用フィールドを収容するため、HTTP本文全体は7 MiBに制限します。認可とCSRF検証を通過した要求では、宣言済みContent-Lengthとストリーミング／チャンク送信の超過はいずれも `413` を返し、読込バッファは上限+1バイトまでです。他のCore POSTアクションの上限65,536バイトは維持します。
- JSONまたはContent-Type不正は `400`、認証／Origin拒否は `403`、適用時の古い版との競合は `409` を返します。
- Coreマップが処理するすべての応答に `Cache-Control: no-store` を付与します。

## 検証

リポジトリルートから、隔離された実HTTPループバック境界テストを実行します。

```bash
dotnet build tests/Sem.EventImportHttpChecks/Sem.EventImportHttpChecks.csproj -c Release
dotnet run --project tests/Sem.EventImportHttpChecks/Sem.EventImportHttpChecks.csproj -c Release --no-build
```

テストは一時的なループバックKestrel、合成Cookie identity／antiforgery token、一時SQLiteデータベース、no-op Push transportを使います。外部エンドポイントには接続せず、本番設定も変更しません。
