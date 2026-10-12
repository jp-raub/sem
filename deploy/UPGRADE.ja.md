# 検証済み更新候補：SQLite移行・本人確認付きパスキー追加

[英語版が正本](UPGRADE.md)。

**イベント管理アプリ全体の完成版ではありません。** 初期外部テスト版に安全なSQLite移行とパスキー追加の本人確認を加えた版です。曲・枠・フェーズ・タイムテーブル・当日進行・QR等は別作業。配布の作成だけではDXP更新・PRマージを行いません。

## 1. 稼働環境を変えずに受領・確認

配布物は不変タグ付きイメージ、固定コミットのソースZIP、英日手順、VERSION.json／txt、verification.json、SHA256SUMSです。CIでlinux/amd64、ストレージ18・期限／状態17・HTTPS移行／認証45項目を検証し、同じイメージを書き出し→再読込み→ID一致と全ファイルのチェックサムを検査します。合成データ・仮想認証器であり、現DXPのDBや物理端末は別途確認します。

配布artifactは**新しいステージングディレクトリ**へ展開し、稼働ディレクトリを上書きしません。その場所で実行します。

```sh
sha256sum -c SHA256SUMS
python3 -c 'import json; v=json.load(open("VERSION.json")); print(v["sourceCommit"], v["image"], v["imageId"], v["platform"])'
gunzip -c sem-dxp-image.tar.gz | docker load
# タグはVERSION.jsonから転記。下記は置換必須です。
docker image inspect sem-dxp:proof-REPLACE_WITH_COMMIT_PREFIX --format '{{.Id}} {{.Os}}/{{.Architecture}}'
```

コミットを配布CIと照合し、イメージID／platformがVERSIONと一致することを確認。不変タグの読込みで旧 `sem-dxp:external-test` は置換されず、アプリも再起動しません。ソースを稼働場所へ重ねず、prepare-source.pyを実行せず、.env.exampleで既存.envを上書きせず、VAPID／保護鍵を再生成しません。

## 2. 現設定と新しい停止中バックアップを保持

実操作は既存の `/volume2/Docker/sem-dxp-delivery/deploy` で行います。現Compose・.env・SMTP認証ID・SMTP_FROMのアドレス／表示名・NPM信頼IP・brのsem別名・公開ホスト名／RP ID・Push鍵を維持します。今回Cloudflare Access・NPM・DNSを変更しません。

秘密情報を出さず、稼働コンテナを確認します。

```sh
cid=$(docker compose --env-file .env ps -q sem)
test -n "$cid"
docker inspect --format 'project={{index .Config.Labels "com.docker.compose.project"}} image={{.Image}}' "$cid"
docker inspect --format '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Type}} {{.Name}} {{.Destination}}{{end}}{{end}}' "$cid"
docker compose --env-file .env exec -T sem sh -c 'id; ls -ldn /data /data/keys; ls -ln /data/sem.db'
old_image_id=$(docker inspect --format '{{.Image}}' "$cid")
```

元ボリューム `sem-external-test_sem-data`、UID/GID1654:1654と実際の値を照合。不一致なら停止して手順を見直します。旧イメージIDを記録し、そのイメージも保持。[BACKUP.md](BACKUP.md)／既存README第7節の手順で**新しい**停止中/data全体バックアップを作り、元サービスへ戻します。gzip、既存チェックサム、DBと実際の鍵ファイルを確認し、別の保護された保管先にも保持。前回復元テストのファイルが最新状態とは限りません。元ボリュームを候補版へ直接渡しません。

## 3. 別の空ボリュームへオフライン復元

正しい新規バックアップ・取得済み旧イメージID・未使用の新ボリューム名を選んでから実行します。以下は確認・置換が必要な例です。元ボリューム削除やdown -vは禁止です。

```sh
backup='backups/REPLACE_WITH_CONFIRMED_FRESH_ARCHIVE.tgz'
candidate_volume='sem-proof-check-REPLACE_WITH_UNIQUE_SUFFIX'
(
  set -eu
  gzip -t "$backup"
  sha256sum -c "$backup.sha256"
  if docker volume inspect "$candidate_volume" >/dev/null 2>&1; then
    printf 'Volume already exists; choose a new unused name.\n' >&2
    exit 1
  fi
  docker volume create "$candidate_volume"
  docker run --rm -i --network none --cap-drop ALL \
    --security-opt no-new-privileges:true --entrypoint sh \
    -v "$candidate_volume:/data" "$old_image_id" \
    -c 'test ! -e /data/sem.db; test -z "$(find /data -type f -print -quit)"; tar xzf - -C /data; id; ls -ldn /data /data/keys; ls -ln /data/sem.db' < "$backup"
)
```

旧イメージの空/dataの所有権が新ボリュームへコピーされ、通常appユーザーで展開します。root指定・chmod777・ホストの保存先マウント・権限制限撤廃は使いません。失敗した部分展開先を起動せず、非空ボリュームへ再展開しません。

展開DB・各鍵のハッシュをアーカイブと照合し、前回と同じく**読み取り専用の一時DBコピー**でSQLite integrity_check／foreign_key_checkを検査します。DB・鍵の内容を共有しません。候補起動は移行を書込み、復元された未配信通知を送る可能性があります。ハッシュ／整合性一致だけではログイン等の受入完了ではありません。

## 4. 切替前にoverrideを検証

ステージングしたソースから `deploy/upgrade-candidate.override.yaml` だけを既存deployへコピー。VERSIONのタグと確認済み候補ボリュームを非秘密のシェル変数に設定し、.envを変えません。

```sh
export SEM_CANDIDATE_IMAGE='sem-dxp:proof-REPLACE_WITH_COMMIT_PREFIX'
export SEM_CANDIDATE_VOLUME="$candidate_volume"
docker compose --env-file .env -f compose.yaml -f upgrade-candidate.override.yaml config --format json |
python3 -c 'import json,os,sys; c=json.load(sys.stdin); s=c["services"]["sem"]; v=c["volumes"]["sem-data"]; assert s["image"]==os.environ["SEM_CANDIDATE_IMAGE"]; assert v["external"] and v["name"]==os.environ["SEM_CANDIDATE_VOLUME"]; assert v["name"]!="sem-external-test_sem-data"; assert any(m["source"]=="sem-data" and m["target"]=="/data" for m in s["volumes"]); assert not s.get("ports"); assert not s["environment"].get("Sem__BootstrapSecret"); print("Candidate image/volume, private port and disabled bootstrap: OK")'
```

展開済みconfigは秘密情報を含むため、そのまま表示／共有しません。overrideは**イメージとデータボリュームのみ**変更し、同じプロジェクト／サービス／公開経路を使います。external指定なので存在しない候補ボリュームを勝手に空で作りません。確認済みsem-data構成用のテンプレートであり、実環境が異なれば見直しが必要です。

## 5. 予定した時間に単一インスタンスで実機受入

短時間の停止と復帰経路を準備してから実行します。**元を停止してから候補を起動**し、brのsem別名で二重稼働させません。

```sh
test "$(docker image inspect sem-dxp:external-test --format '{{.Id}}')" = "$old_image_id"
docker compose --env-file .env stop sem
docker compose --env-file .env -f compose.yaml -f upgrade-candidate.override.yaml \
  up -d --no-build --pull never --force-recreate sem
cid=$(docker compose --env-file .env -f compose.yaml -f upgrade-candidate.override.yaml ps -q sem)
docker inspect --format 'image={{.Image}} {{range .Mounts}}{{if eq .Destination "/data"}}volume={{.Name}}{{end}}{{end}}' "$cid"
```

実イメージID・候補マウント、起動状態とreadyを確認。スキーマ不一致は調査が必要な失敗で、DB削除／履歴改変で通しません。起動失敗時は後述の元環境へ戻します。

**同じ既存HTTPSホスト名**で確認します。

1. 既存管理者／一般ユーザーのログアウト→既存キー再ログイン、プロフィール・受信箱・招待の人数／期限／判断／失効・イベント参加を維持。
2. 端末再登録なしのPush、新しい30秒予約通知、設定済み送信元／表示名で実SMTP到達。サーバーの処理済みと実受信は区別。
3. 既存キー再認証→同じブラウザで保存済みメールのリンク確認→**別の新しいキー／認証器**登録→新キーで再ログイン。既存の除外対象キーを持つ認証器が新規作成を拒否する場合は正常です。iCloud同期は別キー追加と区別。
4. 再送で旧リンク無効、取消し／ログアウトで未完了追加無効、期限切れは再認証から。全体15分・メール再確認後最大5分（全体期限まで）を確認。証明URLを共有しません。
5. 同じoverrideで候補を再起動し、既存／新キー・データ・通知を再確認。Safari／iCloud／iPadOS等の対象端末を個別に記録。

起動だけで全項目完了扱いにしません。テスト中の追加キー・通知・招待等の変更は候補コピーにだけ保存されます。

## 6. 元環境への復帰と継続利用の判断

**一時的な候補テスト**の終了時は候補を停止し、**元Composeのみ**で起動します。external-testタグが記録済み旧イメージを指すことが前提です。

```sh
test "$(docker image inspect sem-dxp:external-test --format '{{.Id}}')" = "$old_image_id"
docker compose --env-file .env -f compose.yaml -f upgrade-candidate.override.yaml stop sem
docker compose --env-file .env -f compose.yaml up -d --no-build --pull never --force-recreate sem
cid=$(docker compose --env-file .env -f compose.yaml ps -q sem)
docker inspect --format 'image={{.Image}} {{range .Mounts}}{{if eq .Destination "/data"}}volume={{.Name}}{{end}}{{end}}' "$cid"
```

旧イメージIDと `sem-external-test_sem-data` へ戻ったことを実マウントで確認し、元の既存キー・データ・SMTP／Push／予約通知を確認。**候補だけに追加したキーは元DBでは使えない**ため、元キーを残します。両ボリューム・バックアップ・候補イメージを保持し、警告を消す目的で鍵を削除しません。

追加移行後のDBを旧マイグレーション対応バイナリに渡したり、テーブル削除・履歴改変で通したりしません。保持した元ボリューム／DB＋鍵バックアップへ戻し、切替後変更の扱いを明示します。後で候補を継続利用するなら、activeなイメージ／ボリュームを記録し、日常の再起動・バックアップも正しいoverrideを使います。次の再起動で黙って旧DBへ戻さないでください。本適用は別の受入判断です。
