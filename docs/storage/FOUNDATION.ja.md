# mock起点の保存基盤：旧Identity・モデル・migrationの分離

## 今回の到達点と接続しない境界

固定mockのUIへ実機能を接続する前段として、保全済み旧main `812b2784212e9015ecbbce31280e48cb0bc65e84` の保存モデルを `src/Sem.Storage` へ分離した。技術基盤は従来の.NET 10・Identity・EF Coreを維持し、独自の認証ストアにはしない。

**公開ホスト `Sem.Web` はこのライブラリをまだ参照しない。実DB・鍵・volumeの読書き、migration自動適用、ログイン、業務保存、メール／Push workerの起動は追加していない。** `/about` と `mock-ui-ready` のデモ版説明も変更しない。これは全機能版への切替ではない。

要件IDはF02、D01〜D05、T01・T04・T06、N01〜N03。要件正本はNotion、画面と接続順は[UI対応表](../ui/IMPLEMENTATION-MAP.ja.md)、この資料は内部実装・検証境界を記録する。旧物理モデルを最新要件へ完全対応済みと主張しない。

## 保持したもの

- `ApplicationDbContext`、`SqliteStorageInitializer`、migration関連89ファイルは元ソースのバイト列をそのまま保持（合計91ファイル）。適用済みmigrationの内容・ID・snapshotを変更しない。
- 有効migrationは**43件**。ほかに、旧ソースで既に非発見扱いだった `00000000000000_CreateIdentitySchema` のテンプレート対も保存する。ファイル対の数を有効migration数と取り違えない。
- DBモデル77型を元の31ソースからC#構文木で抽出。プロパティ・属性・継承・namespaceを維持し、同じファイルにあった業務サービス・配送・画面・起動処理を持ち込まない。未使用の `using WebPush` だけを除き、配送パッケージへの依存を作らない。
- モデル名／namespaceと既存DBのテーブル・キー・参照・制約・migration履歴を維持する。IdentityユーザーID・SecurityStamp・passkeyストア・認証失効情報を再生成しない。
- 由来・元ファイルのSHA-256・抽出後のSHA-256・migration IDは `model-provenance.json` に固定。`storage-source-checks.py` が脱落・意図しない差分を検出する。意図的にモデルを変更する際は由来を偽ってhashだけ更新せず、追加migrationと差分記録を用意する。

## 合成データによる集中検証

`dotnet run --project tests/Sem.Storage.Checks -c Release`

メモリ内SQLiteと、その場で生成・削除する合成ファイルDBだけを使う。実メールアドレス・有効なWebAuthn資格情報・実鍵は不要。

| 検証 | 確認すること |
| --- | --- |
| 新規初期化 | Identity schema Version3のモデルとsnapshot一致、43 migration適用 |
| 再初期化 | 全schema／全行の指紋が不変。ID・管理者・stamp・合成passkey・失効情報を保持 |
| 履歴のない既知baseline | 正確な既知schemaだけを採用し、最新まで更新してID・stamp・passkeyを保持 |
| 合成ファイルの隔離コピー | baselineデータ入りコピーを最新へ更新し、元ファイルのSHA-256を変更しない |
| 未知DB・未知migration・履歴欠落・schema差 | 拒否し、既存schemaとデータの指紋を変えない |

初回にmigration数の誤った期待値、テストSQLのJSON文字列の扱い、awaitを跨ぐspan比較を修正した。これらの失敗は成功へ流用せず、最終ソースで22検査を再実行している。

検証は実DBの全業務データ移行、実鍵での認証継続、物理パスキー、PostgreSQL、全業務ルールの回帰ではない。ライブラリを公開バイナリへまだ接続していないため、既存の公開readinessをDB準備済みの証拠にも使わない。

## 抽出の再現

開発用の任意の空ディレクトリへ固定旧ソースを展開してから実行する。稼働DBのパスを引数に取るツールではない。

```sh
mkdir -p /tmp/sem-old-source
# リポジトリで固定旧commitを取得済みであること
git archive 812b2784212e9015ecbbce31280e48cb0bc65e84 src/Sem.Web | tar -x -C /tmp/sem-old-source
dotnet run --project tools/Sem.ModelExtractor -- /tmp/sem-old-source/src/Sem.Web /tmp/sem-storage-extracted
python3 scripts/storage-source-checks.py
```

抽出ツールは.NET SDK同梱のRoslynを使用する。モデルと保存ファイルの再抽出が一致することもローカルで確認した。移植元の固定commitの由来は、抽出先の任意のソース内容から推測せず、上記 `git archive` の固定refを用いる。

## 次の接続

1. 独立した認証サービス登録と設定検証を用意する。`IdentitySchemaVersions.Version3`、同じIdentityユーザー型・migration assembly、永続Data Protection鍵・Cookie、公開Origin／RP／信頼プロキシの契約を合わせる。
2. 旧ログインを単独コピーせず、失効検証、セッションclaim、CSRF、Origin、入力上限、レート制限、登録途中／復旧の限定状態を必要範囲で移植する。デモ切替とlocalStorageを実認証へ使わない。
3. 隔離した合成環境で認証APIと固定mockのログインUIを接続・回帰する。実データ接続の前に保全・互換・復元の必要な検証と、デモ／実機能の分離を確認する。
4. この段階の未接続ガードを変更するPRで、公開ホストの実データ利用開始を明記する。起動すれば無条件に実DBへmigrationする変更を紛れ込ませない。

PostgreSQL切替と日次保全は要件として残すが、今回のSQLite移植で実装完了にはしない。UI資産171ファイルは変更せず、旧Razor UIや旧full一式は戻さない。
