# SEM — mock起点の再構成

採用UI MOCK v33（固定commit `9cf8b031258fa1c9afe9668f54e61f002c4bce17` の `mock/`）を基準に再出発するUIデモ版です。

**旧製品の認証・DB・登録・通知機能は接続していません。モック内の成功は模擬動作です。** 実際の個人情報を入力しないでください。UIを基準に実機能を後続接続します。

## 実行
`dotnet run --project src/Sem.Web`

- `/`：採用モック（元資産171ファイルをバイト一致で保持）。フッターだけ稼働版リンクを追加。
- `/about`：人向け稼働版・デモ版の説明。
- `/api/version`：ビルド時に固定した完全SHA＋公開run／attempt。未設定時は開発版。
- `/health/ready`：モック資産のhash一致。DB準備状態ではない。

旧mainは `backup/main-before-mock-first-20261026`（`812b2784212e9015ecbbce31280e48cb0bc65e84`）に保全。履歴は巻き戻さず置換commitで移行します。既存volume・鍵・DBを読み書きせず、CD取得側の切替前保全と隔離復元検査は維持します。

## 検証と公開
必須CIはモック固定一致・全JS構文・版APIの厳密契約と代表画面の実ブラウザー。旧UI専用fullは本構成では実行しません。CDは既存の候補・現在main・必須job・run／attempt・linux/amd64・digest検証を引継ぎます。公開後は毎回別nonce付き版＋mock-ui-readyを3回連続照合。全レプリカ・実業務機能の証明にはしません。
