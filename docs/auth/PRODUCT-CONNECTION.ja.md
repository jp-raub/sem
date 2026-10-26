# 固定ログインUIへの製品接続

## 今回の範囲

採用mock171資産をバイト不変で保持し、**明示した実認証モードだけ**、そのindex shell・全CSS・最終login section／メニュー／dialogのDOM構成に製品用adapterを接続する。基底app.jsの古いパスコード画面やdemoロール選択は使わない。静的mockのHTMLを一式別UIへ書き換えたり、実ユーザーを架空のdbへ混ぜたりしない。

既存アカウントのメール指定→標準パスキー→本人の最小セッションDTO→ログアウトを接続した。外部日本語JSONの入力ヘルプと、操作中・非対応・認証器での中断・通信失敗を同じlogin DOM内に表示する。メール・認証状態・資格情報をWebStorageへ保存しない。未接続の保存済み配色は推測せず、ログイン段階はmock既定green＋システムの明暗を使用し、OS変更にも追随する。個人／イベント／サイトの保存済みテーマ接続は後続。新規登録／追加／復旧／メール変更／メールなしログインはまだ接続しない。

ログイン後のイベント一覧・プロフィール・申込み・保存等は後続。架空の一覧や模擬成功を見せず「まだ接続していません」と表示する。実認証モードでは元のdemo scriptや別HTMLへの直読入口も404とし、認証adapterだけを実行する。版・about・新鮮なUI資産readinessは維持する。`mock-ui-ready` は引き続き固定UI資産の契約であり、全業務機能の準備完了ではない。

**既定の公開配置はdemoのまま。今回のイメージ公開を、実volumeへの接続・公開ログイン有効化・実端末受入と同一視しない。** この変更自体は運用者の実DB・鍵・秘密設定を読み書きしない。

## 配置の分離

|環境変数|実認証モードの契約|
|---|---|
|`SEM_APPLICATION_MODE`|未指定または `demo` はDB非接触の画面デモ。`authenticated` だけが実認証。未知値やmodeなしでの実配置値混在は起動拒否|
|`SEM_AUTH_ORIGIN`|明示HTTPS Origin。既定のホスト名は持たず、診断へ値を出さない|
|`SEM_AUTH_DATABASE`|既存SQLiteファイルの絶対パス。存在しないDBを自動作成しない|
|`SEM_AUTH_KEYS_DIRECTORY`|既存Data Protection鍵の絶対directory。空・不正XML・外部実体参照・リンクを拒否|
|`SEM_AUTH_BACKUP_DIRECTORY`|事前作成した別保全directoryの絶対パス。DB／鍵との包含・重複やリンクを拒否|
|`SEM_AUTH_TRUSTED_PROXIES`|任意。カンマ区切りIP。未指定は転送を一切信頼せず直接TLSだけ。明示IPだけ1段のHTTPS転送を信頼|

配置値の自動設定・既存volumeの自動探索・旧データの初期化・本番用アカウント生成は行わない。Linuxの協調単一writerを前提とする。旧アプリ／worker／保守処理は停止し、同じ `sem.runtime.lock` を使用する必要がある。非協調プロセスの停止や全replicaの排他を、このleaseが証明するわけではない。

既存volumeの接続は、運用者がDBと鍵を一式保持していることと、配置設定・停止／保全を確認してから別途有効化する。ソースの退避branchだけではDB／鍵保全の代わりにならない。秘密値をPR・チャット・ログ・成果物へ貼らない。

## 起動時の保全と拒否

1. 明示パス・symlinkを検査し、DB親directoryの旧互換leaseを保持する。lock inodeは解放時にもunlinkしない。
2. 起動前にSQLiteのread-only接続＋backup APIでWALを含む確定DBを別copyへ取得する。鍵XMLも一式copyし、directory700／file600で保存する。
3. copyだけで標準Data Protection鍵ring（自動生成禁止）とencryptorの生成可否、および旧43 migrationの**全適用済み**履歴・schema・整合性を検証する。未適用migrationや未知schemaがあれば原DBを更新せず起動拒否。新規DBの作成・起動時の自動移行は、この段階では提供しない。
4. DB／鍵のhashを含む `verified.json` を最後に記録する。記録のない失敗copyは成功扱いにしない。原DB／鍵や失敗copyを自動削除しない。
5. leaseを製品ホスト終了まで保持し、標準Identityを既存DB／鍵へ接続する。起動copyは最大32式で停止し、再起動失敗ループによる無制限増加を防ぐ。上限時は運用者が検証済み保全の別退避と保持方針を確認する。自動の世代削除はまだ行わない。

これは定期backup／復旧運用全体の置換ではない。旧DBの移行が必要なら、停止・別copyでの移行と一式復元を別工程で実施する。原DBへschema差を強行適用しない。

## 高速な検証

```sh
dotnet run --project tests/Sem.Hosting.Checks -c Release
dotnet publish src/Sem.Web -c Release -o /tmp/sem
dotnet publish tests/Sem.Authentication.Fixture -c Release -o /tmp/sem-auth-fixture
SEM_PRODUCT_DLL=/tmp/sem/Sem.Web.dll SEM_AUTH_FIXTURE_DLL=/tmp/sem-auth-fixture/Sem.Authentication.Fixture.dll node scripts/product-authentication-browser.cjs
```

.NET・Playwright Chromium・OpenSSLが必要。合成fixtureで標準登録した仮想パスキーをメモリに保持し、fixtureを停止後、**実製品Sem.Web**を別localhost HTTPSプロキシの後ろで起動する。実製品のブラウザー操作で署名ログイン・ログアウト・失効Cookie拒否・プロセス再起動を検査する。fixture登録／変更APIは製品には存在しない。

全データは自分で作った一時領域の合成人物・合成DB・合成鍵だけ。HTTPSの合成TLS秘密鍵は所有者限定の一時領域へ置き、finallyでホストを終了して自分の領域だけを削除する。WebAuthn秘密鍵・Cookieはファイル保存せず、外部通信・個人情報入力・実Pushはしない。32式上限や不正schema・不正設定・symlink・鍵XML外部参照は失敗することを検査し、保全DB／鍵からの復元も確認する。

最初の試行では、隔離copy検査側のIdentity schema3設定不足を検出して修正した。失敗した試行を成功へ流用せず、新規合成DBから再実行する。不正な鍵descriptorを標準parserが例外化する場合も、秘密値を出さない固定分類で起動拒否する。

ヘルプ追加後は送信ボタンを `.primary` で明示し、ヘルプを無効化／送信と誤認しないことも再検証する。

既存の認証44／HTTPS52検査は維持し、関連変更・未知差分だけ配置と製品UI検査を加える。必須job名・8分上限・固定Playwright環境・公開候補の保護は変更しない。

最終候補の配置・保全36検査と実製品UI43検査（クライアント例外0・外部通信0）が成功。4幅×明暗で最終mockのpanel／入力／送信／ヘルプの寸法・字体・配色を比較した。固定mock171資産と既存72表示・22タブ・代表4モーダル322検査も成功。これは全UI状態のピクセル一致や物理認証器・実volumeの受入ではない。
