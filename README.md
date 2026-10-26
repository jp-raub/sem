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

## 実機能への接続基準
[画面・主要状態と旧機能の接続順](docs/ui/IMPLEMENTATION-MAP.ja.md)に、固定モックの18経路×4ロール、イベント内タブ、主要なモーダル／状態、旧mainからの再利用境界を記録しています。基準は全スクリプト適用後のUIで、古い`app.js`だけを移植しません。

`python3 scripts/ui-inventory-checks.py` と `node scripts/ui-inventory-browser.cjs` を既存CIに追加しています。実ブラウザーの画面基準はUIデモの回帰であり、認証・DB・業務保存が完成した証拠ではありません。次は認証・データ基盤を既存DB／鍵の互換を保って接続します。

### 保存基盤の分離
[旧Identity・モデル・migrationの保存基盤](docs/storage/FOUNDATION.ja.md)を`Sem.Storage`へ分離しました。合成DBで互換・保持・拒否を検証しますが、公開ホストからはまだ参照せず、実DB／鍵の利用や実認証は開始していません。
