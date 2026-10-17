# DXP 外部テスト配置ガイド

**GitHub mainを正本とする開発・受入テスト用の配置ガイドです。更新時は既存の永続データと鍵を維持します。**

現行の機能・役割分担・更新前バックアップと具体的な受入手順は [ACCEPTANCE-TEST.ja.md](ACCEPTANCE-TEST.ja.md) を参照してください。

## 対象と範囲

- DXP 4800 Plus / linux amd64 / Docker Compose。
- 公開URLは `https://sem-test.raub.jp/` 固定。LAN内も同じURLを使います。
- 既存ネットワーク `br`、NPM → `http://sem:8080`。ホストポートは公開しません。
- 空のSQLiteから、保護された初期設定→実メール確認→実パスキー登録→ログインを確認する実装です。
- 招待依頼・判断コメント・所有者限定リンク、本人パスキー／プロフィール管理、管理者アカウント管理、イベント・曲・時刻表・参加matrix・設問・コメント・受付／QR・当日進行・通知を実装しています。
- モックのデモ用ユーザー切替やlocalStorageは移植しません。機能の実装、自動検証、DXPでの物理端末・実配送の受入は別々に管理します。

## 1. ファイルの配置

引き渡したソースをDXPの任意の専用ディレクトリへ展開します。`deploy/` の親に `src/` がある構造を維持してください。

```sh
cd /path/to/sem/deploy
cp .env.example .env
chmod 600 .env
```

`.env` の秘密情報はチャット・Git・公開画面に出さないでください。`docker compose config` は展開済みの秘密情報を表示するため、その出力を共有しないでください。

## 2. NPMの信頼するIPとSMTP

```sh
docker network inspect br
openssl rand -hex 32
```

最初のコマンドでNPMコンテナのIPv4を確認し、`SEM_TRUSTED_PROXY_IP` に設定します。2番目の出力を `SEM_BOOTSTRAP_SECRET` に設定します。SMTPの接続先・ポート・ユーザー名・パスワードを設定し、送信元 `misc@raub.jp` が許可されることを確認してください。

IPを再割り当てした場合は.envのIPを更新してコンテナを再作成します。ネットワーク全体を無条件に信頼する設定にはしません。

## 3. イメージとPush配信鍵

```sh
# 同梱イメージを使う場合（ファイル位置に合わせて変更）
gunzip -c ../sem-dxp-image.tar.gz | docker load
# ソースから作る場合はこちらを使用
# # 同梱イメージを使う場合（ファイル位置に合わせて変更）
gunzip -c ../sem-dxp-image.tar.gz | docker load
# ソースから作る場合はこちらを使用
# # 同梱イメージを使う場合（ファイル位置に合わせて変更）
gunzip -c ../sem-dxp-image.tar.gz | docker load
# ソースから作る場合はこちらを使用
# docker compose --env-file .env build
docker compose --env-file .env run --rm --no-deps sem --generate-vapid
```

出力される2つの鍵を.envに設定します。秘密鍵は公開しません。一度登録した端末がある状態で鍵を作り直すと端末の再登録が必要です。

```sh
docker compose --env-file .env up -d
docker compose --env-file .env logs --tail 50 sem
```

## 4. NPM

- Domain Names: `sem-test.raub.jp`
- Scheme: `http`
- Forward Hostname: `sem`
- Forward Port: `8080`
- SSL: 有効な証明書 / Force SSL
- 元のHostを保ち、`X-Forwarded-Proto: https` と `X-Forwarded-For` を設定してください。通常のNPM proxy host設定を前提にしています。
- 公開側にキャッシュを追加しないでください。

cloudflaredやLAN内DNSの既存経路は変更しません。アプリは不明なHostやHTTPS転送が確認できないアクセスを拒否します。

## 5. 空状態から初期設定

1. 同じ公開URLをSafariで開く。
2. サイト名・表示名・管理者メール・初期設定用秘密情報を入力。
3. `misc@raub.jp` からの確認メールを開く。
4. 同じURLでパスキーを登録。完了した時点で最初の全体管理者が有効になります。
5. `.env` の `SEM_BOOTSTRAP_SECRET` を空にして `docker compose up -d --force-recreate sem`。

初期設定完了後は、秘密情報が残っていても2人目の初期管理者を作成できません。確認メールのURLは1時間・1回限りです。到達しない場合、同じメール・初期設定用秘密情報（一般登録は同じ招待リンク）で再送できます。再送時は旧メールの確認URLを無効にします。到達しない場合、同じメール・初期設定用秘密情報（一般登録は同じ招待リンク）で再送できます。再送時は旧メールの確認URLを無効にします。到達しない場合、同じメール・初期設定用秘密情報（一般登録は同じ招待リンク）で再送できます。再送時は旧メールの確認URLを無効にします。パスワードログインや招待なしの一般登録は公開していません。

## 6. 端末テスト

- macOS Safari: 登録→ログアウト→パスキーで再ログイン→追加パスキー。
- iOS/iPadOS: 同じURLでログイン。PushはSafariの共有メニューから**ホーム画面に追加**し、そのアプリから許可・端末登録・テスト通知。
- iCloudで同期された同じパスキーと、別のパスキーを追加する操作は異なります。両方を区別して確認してください。
- メールテスト、30秒後の予約通知、ページを閉じた受信、Pushをタップしたメッセージボックスへの復帰、既読化。
- 招待1名のリンクを作り、別アドレスで登録。完了後にリンクが人数上限で失効すること。
- リンクの緊急失効、期限失効、一般/イベント管理者による直接発行の拒否。
- コンテナ再起動後、ユーザー・招待・通知・パスキーが残り、再ログインできること。

Push配信の外部事業者側や実SMTPアカウントの到達性は、実機で最終確認が必要です。送信待ち・失敗回数は配信状況で確認できます。配信は再試行するため、障害時に外部送信だけ成功して記録が失敗した場合、重複配信の可能性があります。複数コンテナ同時稼働はこのテスト版の対象外です。

## 7. バックアップ / 復元

英語の正本は [BACKUP.md](BACKUP.md)。この文書修正だけでは配布イメージ・DXP環境は更新されず、復元成功を意味しません。

SQLiteとData Protection鍵を必ずセットで保存します。停止中に `/data` 全体をバックアップします。`cap_drop: ALL` のためroot指定でもapp所有ファイルを読めるとは限りません。通常のアプリユーザーで読み取り、圧縮stdoutをDXP側ユーザーで保存します。`--user root` やバックアップ先のバインドマウント、chmod 777、アプリの権限制限撤廃は使いません。バイナリ出力には `-T` が必要です。

既存の `deploy` ディレクトリで実行します。サブシェル内の終了処理で、成功・失敗時とも停止後のサービス起動を試みます。失敗したアーカイブは `.partial` のまま残り、復元には使いません。起動を試みたことと正常稼働は別なので、終了後に状態を確認してください。

```sh
(
  set -eu
  mkdir -p backups
  umask 077
  backup="backups/sem-data-$(date +%Y%m%d-%H%M%S).tgz"
  test ! -e "$backup"
  test ! -e "$backup.partial"
  docker compose --env-file .env stop sem
  trap 'docker compose --env-file .env up -d sem' EXIT
  docker compose --env-file .env run \
    --rm --no-deps -T --entrypoint sh sem \
    -c 'tar czf - -C /data .' > "$backup.partial"
  gzip -t "$backup.partial"
  tar -tzf "$backup.partial"
  mv "$backup.partial" "$backup"
  sha256sum "$backup" > "$backup.sha256"
  printf 'バックアップ作成完了: %s\n' "$backup"
)
docker compose --env-file .env ps sem
```

一覧に `./sem.db` と `./keys/` 内の実際の鍵ファイルが含まれることを確認します。ディレクトリだけでは不十分です。読み取り・チェックサムの確認は、DB整合性や復元成功の証明ではありません。チェックサムはアーカイブと一緒に保管しますが、同じ場所のチェックサムだけでは独立した真正性の保証になりません。

バックアップには個人情報・認証関連情報・鍵が含まれます。アクセスを制限し、公開ディレクトリには置かず、別の保護された保管先にも保存してください。最初に失敗した `sem-data.tgz` や `.partial` は復元に使用しません。鍵の中身・SMTP資格情報・Push秘密鍵・完全な購読URLを共有しないでください。

### 復元前の読み取り専用確認

成功した日時付きアーカイブを明示的に選び、`gzip -t`、`tar -tzf`、`sha256sum` で確認します。最新ファイルが成功したものとは限りません。既存チェックサムを上書きして検証を通してはいけません。

秘密情報を含む環境変数は表示せず、実コンテナを確認します。

```sh
docker compose --env-file .env ps sem
cid=$(docker compose --env-file .env ps -q sem)
if [ -n "$cid" ]; then
  docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "$cid"
  docker inspect --format '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Type}} {{.Name}} {{.Destination}}{{end}}{{end}}' "$cid"
  docker compose --env-file .env exec -T sem sh -c 'id; ls -ldn /data /data/keys; ls -ln /data/sem.db'
else
  printf '稼働中のサービスがありません。対象プロジェクトを確認してから進めてください。\n' >&2
fi
```

実プロジェクト名・実ボリューム名・イメージ・UID/GIDとアーカイブ内容が確定するまでは、具体的な展開・切替コマンドは実行しません。Composeの宣言だけから実ボリューム名を決めつけないでください。

### 復元テストの合格条件（DXPでは未検証）

1. 切替前に新たな停止中バックアップを取り、元ボリュームと戻し方を確保する。
2. 別の空ボリュームへDBと鍵をセットで復元し、確認した実行UID/GIDに所有権を合わせる。元ボリュームへ上書きしない。
3. 起動前に保存領域を検証する。アプリ起動はDB書込み・移行・予約通知送信や外部サービスへの接続を伴う可能性がある。
4. 元アプリを停止してから復元版を既存公開経路／ネットワーク別名 `sem` へ接続する。2台同時接続しない。NPM・DNS・公開ホスト名・VAPID鍵は変更しない。
5. 同じHTTPSホスト名でユーザー・招待・通知履歴、ログアウト→既存パスキー再ログインを確認する。端末再登録なしのPush、新しい予約通知も確認する。サーバー送信済みと実機受信を区別する。
6. 元環境へ戻す場合は先に復元版を停止する。切替後の変更は復元側だけにあるため、元側へ自動反映されるとは考えない。採用した稼働ボリュームと結果を記録し、完了まで元ボリュームを残す。

## 8. 終了

```sh
docker compose stop sem
```

NPM側の公開設定も停止してください。`docker compose down -v` はテストDBと鍵を削除するので、保存が必要なときは実行しないでください。
