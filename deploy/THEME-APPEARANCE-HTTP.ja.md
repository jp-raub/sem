# テーマ外観 HTTP API

HTTP境界は共有の `/api/core/{action}` マップ内に実装する。独立したルートは追加せず、共通のOrigin検証およびアンチフォージェリ検証を迂回しない。

## 外観の読み取り

`GET /api/core/theme-appearance` は、現在認証されているユーザーの外観を返す。任意の `id` クエリでイベントを指定できる。

```text
GET /api/core/theme-appearance
GET /api/core/theme-appearance?id=<eventId>
```

`id` を指定する場合、空でないこと、128文字以下であること、制御文字を含まないこと、および1回だけ出現することが必要。イベントの可視性とアクセス判定には、既存の `ParticipantEvent` 検証を使用する。レスポンスは `{ "eventId": string|null, "appearance": ThemeAppearanceView }`。外観情報には現在の `actorId`、権限フラグ、安全なカタログ情報、呼び出しユーザー本人の個人レイヤー、任意のイベントレイヤー、サイトレイヤー、および解決済みテーマが含まれる。アカウント／ユーザーを選択するパラメーターはない。個人設定の対象は常に現在の認証ユーザーとなる。`GET` の応答には `Cache-Control: no-store` を付ける。

## 外観・カタログの書き込み

すべての書き込みは共有Coreマップへの `POST` で行い、1つだけのクエリパラメーター `expectedActor` に現在のactor IDを指定する。これは操作の同意・コンテキストを確認する値であり、認証トークンやactorの上書きには使わない。

```text
POST /api/core/theme-personal?expectedActor=<actorId>
POST /api/core/theme-event?id=<eventId>&expectedActor=<actorId>
POST /api/core/theme-site?expectedActor=<actorId>
POST /api/core/theme-catalog-save?expectedActor=<actorId>
POST /api/core/theme-catalog-save?id=<catalogId>&expectedActor=<actorId>
POST /api/core/theme-catalog-delete?id=<catalogId>&expectedActor=<actorId>
```

各IDおよび `expectedActor` は、単一の空でないクエリ値で、128文字以下、制御文字なしでなければならない。`theme-catalog-save` は新規作成時に `id` を省略する。空、重複、不正な `id` は拒否する。カタログIDは既存サービスとデータベース制約でも追加検証する。

JSON本文には `expectedActor` やactor識別子を含めない。

| アクション | JSON本文 | 結果 |
| --- | --- | --- |
| `theme-personal`、`theme-event`、`theme-site` | `ThemePreferenceEdit`: `themeId`、`mode`、`rowVersion` | `ThemePreferenceView` |
| `theme-catalog-save` | `ThemeCatalogEdit`: `name`、`accent`、`background`、`surface`、`text`、`rowVersion` | `ThemeCatalogEntryView` |
| `theme-catalog-delete` | `{ "rowVersion": number }` | `{ "message": string }` |

本文の未定義フィールド、必須フィールドの欠落、不正なJSON型は拒否する。`mode` には既存の `ThemeMode` 数値 (`0` System、`1` Light、`2` Dark) を使用する。`themeId: null` および `mode: null` を送ると設定レイヤーを解除できる。新規カタログ項目のIDはサーバーが生成し、行バージョンは1で開始する。更新・削除には現在の行バージョンが必要。組み込みカタログ項目は編集できるが削除できず、参照中の項目も削除できない。

アンチフォージェリ検証またはJSON本文の読み取りより前に、読み取り専用の事前認可トランザクションで現在のactorと対象範囲を確認する。個人設定の変更には現在認証されているactorが必要。イベント設定はイベント管理者（全体管理者を含む）、サイト設定およびカタログ変更は全体管理者に限る。その後、エンドポイントは `expectedActor` と現在のCookieプリンシパルを照合する。既存のCore書き込みメソッドも、それぞれのトランザクション内で認可および競合制御を再確認する。

処理されたすべての `GET` および `POST` 応答に `no-store` を付ける。`POST` は共有のOrigin完全一致検証、アンチフォージェリ検証、および実効65,536バイトの本文上限も適用する。読み取りおよび事前認可で永続化データを変更しない。

## エラーと検証

- `400`: IDの不正・重複、JSONまたは本文値の不正、アンチフォージェリ検証失敗、保護対象カタログ項目の削除。
- `403`: 未認証、無効化済み、古いセキュリティスタンプ、失効済みセッション、actor不一致、権限不足。
- `405`: 読み取りアクションへの `POST`、または書き込みアクションへの `GET`。
- `409`: 行バージョンの不一致、またはデータベースの競合・制約違反。
- `413`: 65,536バイトを超えるリクエスト本文。

`tests/Sem.ThemeAppearanceHttpChecks` は、一時SQLiteデータベースとIdentityスキーマV3を使い、合成Kestrel上で実際のCoreマップを検証する。Cookie／セッション認可、イベント可視性と管理者権限、クエリ・本文の厳格な検証、Origin／アンチフォージェリ、設定の保存・解除、カタログの作成・編集・削除保護、`no-store`、読み取り専用動作、リクエストサイズ上限を確認する。並行中のUI変更が凍結するまで実行しないこと。並行編集中は未実行。