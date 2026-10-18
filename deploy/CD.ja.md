# DXP向け取得型CD

## 採用方針と現在の到達点

環境所有者の指示に基づき、**mainの検証成功後にDockerイメージを公開し、DXPが外向きに取得して自動更新する**方式を採用する。最終形はワンクリックのみではなく、一時停止・再開付きの継続的デプロイメント。GitHub mainはコードの正本であり、配備はそのcommitから生成・検査されたdigest固定イメージを使用する。

**公開側とDXP取得サービスの実装候補。GitHubでの実Docker検証・main統合・DXP初回導入・現地受入はまだ別工程。** mainへ取り込んだだけではDXPは変わらず、DXP側の初回導入とresumeが必要。

## フロー

1. `Main integration verification` がmainのpush commitに対して成功する。
2. `CD verified image` がAPIで同じrun／attempt、元リポジトリ、workflow ID／path、`integration-checks` と `acceptance-browser` の成功を再確認する。
3. そのcommitが現在のmainでなければ、古い候補は公開しない。
4. `deploy/Dockerfile` からLinux amd64イメージを1回ビルドする。非root読取、初期DB作成、live／ready各3回、同じvolumeでの再起動を確認する。
5. 検査したローカルイメージそのものをGHCRへpushする。再ビルドして別物を配布しない。
6. digestとcommit・両runの対応を記録した `candidate.json` を、固有タグ `sem-cd-<commit>-<publication run>-<attempt>` のprerelease assetとして公開する。既存releaseを書き換えたり、配備先へ接続したりしない。`latest`タグは配備根拠にしない。
7. 後続のDXP取得サービスは候補とGitHub API上の成功run／commit／workflow／digestの関係を再検証してから更新する。候補があるだけで更新可能と判断しない。

PRでは権限read-onlyで契約検査とDockerビルド／起動検査だけを行う。PRコードやPRのartifactを、書込権限付きの公開jobに持ち込まない。公開jobは検証されたmainのみをcheckoutする。

### 候補の形式

```json
{
  "schema": 1,
  "deployment_mode": "auto",
  "repository": "jp-raub/sem",
  "commit": "40桁のcommit SHA",
  "image": "ghcr.io/jp-raub/sem@sha256:64桁のdigest",
  "platform": "linux/amd64",
  "integration_run": 123,
  "integration_attempt": 1,
  "publication_run": 456,
  "publication_attempt": 1
}
```

このJSONをシェルとして評価しない。DXP側は固定リポジトリ、完全一致のキー／型、値の形式、API上の同一版と成功状態を検証する。候補公開中にmainが進む競合はDXP側でもチェックし、古い候補を新しい版の後に適用しない。run順序ではなく、現在のmainと検証済み候補を照合する。

## DXP側の実装と契約

- root管理の固定プログラムと非公開設定を、systemd timerで5分ごと（最大20秒の分散待ちを追加）に確認する。PR Runnerや外部SSH着信は不要。DXPのsystemd対応・既存配置の実態は初回導入時に検証する。
- 既存Compose project／service、/dataマウント、環境変数、NPM、URLを維持する。Compose全体や他サービスは更新しない。
- 排他lockを取り、停止中／失敗保留中は新候補を適用しない。新しい合格候補は最新にまとめ、二重更新しない。
- pullとイメージのrevision／architecture／non-root検査を停止前に行う。
- SEMを停止し、SQLite・WAL・鍵・画像を含む/data全体、秘密設定、旧イメージを非公開バックアップとして検証する。容量不足や保全失敗なら切替しない。
- `--no-build --no-deps`相当でSEMだけ再生成する。`down -v`、データ初期化、認証リセットは実装しない。正しい永続volumeかを起動前後で検証する。
- 起動後はlive／readyを連続確認し、稼働commit・digest・結果・保全先を非秘密statusへ記録する。公開HTTPSとユーザーの機能受入は別に扱う。
- 新版起動後の失敗は失敗状態を残して再適用ループを止める。旧イメージ単独へ自動downgradeしない。対応するデータ・鍵・設定・イメージを一組として復旧する。
- 検査済みの追加的migrationは保全後に自動適用する。破壊的な変更など特別な更新だけ明示的保留とし、全migrationを一律手動待ちにしない。
- pause／resume／statusを初回から用意する。失敗の解除は単なるresumeと区別する。保全物の自動削除は初版では行わない。容量を事前確認し不足時は旧版を止めず更新を保留する。保持数の管理は後続改善であり、保持し続けられると無条件保証しない。

Dockerを操作する権限はホスト管理相当になり得る。更新サービスをコンテナ化してDocker socketを渡すだけでは、その権限がSEMに限定されるわけではない。root所有の固定設定／コードで対象を制限し、候補から任意のComposeパスやコマンドを受け付けない。設定値やバックアップをログ・GitHubへ出さない。

## 初回設定の役割

AIはコード、ローカル・GitHub検証、main統合、対象版を固定した導入／停止／再開／復旧手順を用意する。環境所有者はDXP側の初回設定と、具体的に依頼された端末確認を行う。その後の毎回のpull／ビルド／再起動／ログ転記をユーザー作業にしない。初回は既存Composeの解決済み設定と稼働コンテナの環境値・/dataを照合して、root所有の非公開JSONへ固定する。`$`を含む設定値はComposeの再展開で変わらないようescapeし、再読取して一致を検査する。既存環境との差異があれば初回設定を失敗させ、アプリは変更しない。

GHCR packageは初回にprivateで作成される。今回の配布物は既存公開ソース由来で秘密を含めないため、匿名pullできるpublic packageを推奨する。必要な最初の可視性設定は初回導入手順に含める。privateのまま運用する場合のみDXPにread:packagesの認証が必要。秘密はNotionやチャットで受け取らず、DXP上で設定する。未確認のpublic化や認証設定を済みと扱わない。

## 無料での運用と実行時間

- mainの既存CI成功を再利用し、CDで全.NET／browserを再実行しない。API照合とDocker固有検査だけを追加する。
- イメージ公開はmain成功時のみ。PRの公開権限、配備Runner、NAS向け公開ポート、有料サービスは不要。
- 古いmainのrunはgateで除外する。公開処理は直列で、途中の公開をcancelしない。通常の稼働済みmainではGitHub API照合は5分に1回で、全runメタデータを毎回取得しない。認証なしAPIのレート制限・障害時にもアプリは止めない。
- GitHub公式説明ではpublic package利用は無料、GHCRのcontainer storage／bandwidthも現時点で無料とされる。ただし将来の保証ではない。Actionsの料金・制限はリポジトリ可視性・runner種別・契約による。追加の有料runner・課金設定は行わない。
- `candidate.json`以外の実行ログ／DB／鍵／メール／Cookie／credentialをrelease assetにしない。

公式資料（確認時点の情報）:
- https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry
- https://docs.github.com/en/billing/concepts/product-billing/github-packages

## 検証と制限

`python3 scripts/cd/release_gate_checks.py`と`python3 scripts/cd/updater_checks.py`はネットワーク・Docker・秘密不要の契約／状態遷移／保全／API照合試験。`bash -n scripts/cd/image-smoke.sh`で構文確認する。PRの`image-validation`はGitHubのLinux runnerで実際のDockerを検証する。`docker_updater_checks.py`は独立した合成Compose project／volumeだけを作成し、コンテナ再生成・既存volume／鍵の保持・停止tarのhash／所有権・別volumeへの復元・ヘルス失敗時の停止／再適用禁止を確認する。GHCR未公開のPRでも動くよう、テストアダプターだけがローカルイメージを使う。運用CLIにそのバイパスはない。

DockerのないAIローカル環境でDocker動作やGHCR公開の成功を主張しない。GitHubのCI結果、main公開結果、DXP更新、ユーザー受入を段階別に追跡する。本書だけで自動配備が稼働するわけではない。


## 特別な更新の保留

`deploy/cd-policy.json` は通常 `{"mode":"auto"}`。破壊的な移行・特別な手順が必要な変更は、その変更と同じcommitで `manual` を指定し、具体的手順を用意する。公開候補にもmodeを含め、取得サービスは`manual-required`と一時停止を記録し、pull／停止／更新をしない。resumeだけで特別更新を強行しない。これは破壊的migrationの自動解析器ではなく、変更作者・レビューが設定する公開契約である。

## 初回導入手順（main統合後に対象SHAを引き渡す）

以下は導入時の手順。PR作業中はまだ実行せず、Notionの初回設定タスクに対象のmain SHAとCI結果が届いてから行う。GitHub上の固定SHAのコードだけを使い、秘密値は端末上に保持する。

1. DXPでPython 3.9以上、Docker Compose v2以降、systemd、rootによるDocker操作が使えることを確認する。Docker socketのchmodは不要。既存SEMが正常稼働している状態で始める。
2. GHCRで`jp-raub/sem` packageが公開されていることを確認する。初回だけ必要ならpackage settingsでPublicへ変更する。privateのままの場合、rootのDockerへread:packages認証を端末上で設定する。トークンをコマンド引数やNotionへ書かず、`--password-stdin`を使う。
3. 引き渡されたmainの40桁SHAをcheckoutし、`git rev-parse HEAD`で一致を確認する。`scripts/cd/install.sh`とunitをその版で確認する。
4. 既存の全Composeファイルとenvファイルを指定する。パスは現在のDXP配置に合わせる。例の`/path/to/...`は置換し、存在しない場合は実行しない。

```sh
sudo bash scripts/cd/install.sh \
  --project sem-external-test \
  --compose /path/to/current/compose.yaml \
  --env-file /path/to/current/.env \
  --backup-root /volume2/sem-cd-backups
```

Compose overrideを従来から使っている場合は`--compose`を使用順に繰り返す。envファイルを使っていなければ`--env-file`を省く。既存配置のパスや秘密値は共有しない。初回に解決済み設定と稼働環境が一致しなければ止まり、コンテナを変更しない。

バックアップ先と親ディレクトリはroot所有・group/world書込不可で、/dataの内側以外にする。既存の共有フォルダーの権限をむやみに変えず、専用の保護ディレクトリを用意する。volume名は既存コンテナから捕捉し、初回設定で新規volumeを作らない。

5. 初回はpausedのままtimerを導入する。`sudo sem-cd status`で既存コンテナの`observed_running`、`observed_commit`（旧イメージにlabelがなければnull）、pausedを確認する。`current_*`は最後に更新成功した記録であり、失敗時の実稼働を保証しない。
6. `sudo sem-cd resume`、続いて`sudo systemctl start sem-cd.service`で最初の確認を行う。以後は5分ごとに自動実行される。新しいmainのCI／イメージ公開にも時間が必要で、merge直後に即座に反映される保証ではない。
7. `sudo sem-cd status`でready、対象commit、digest、observed_runningを確認する。公開HTTPS・既存パスキーログイン・既存イベント／参加・pause/resumeは、別の対象版付き承認依頼で確認する。

### 一時停止・再開・失敗

```sh
sudo sem-cd pause
sudo sem-cd status
sudo sem-cd resume
```

操作は同じ排他lockを使い、実行中の更新を途中で割り込まない。更新中ならbusyを返すので完了後に再実行する。pauseは通常の次回更新を抑止する。systemd serviceを途中でkillする方法を通常のpauseにしない。

新版起動後の失敗や中断はblockedとなる。resumeだけではblockedを解除しない。初回導入後のプロセス／OS再起動でも失敗候補を勝手に再適用しない。`sudo journalctl -u sem-cd.service -n 30 --no-pager`は固定の非秘密statusが中心だが、設定全文やバックアップは共有しない。

復旧は[BACKUP.md](BACKUP.md)に従い、非公開statusのbackup先、`data.tar`、`old-image.tar`、`container.private.json`、`compose-resolved.private.json`を対応付ける。新スキーマに旧イメージだけを適用しない。必要な復旧・原因対処を終えてからpause状態で`sudo sem-cd acknowledge-failure`し、明示resumeする。acknowledgeは復旧の代行や安全確認ではない。

updater自体の更新は、検査済み固定版のinstallを再実行する。実行中更新があれば失敗し、先に完了を待つ。既存の秘密設定・失敗状態は上書きせず、制御コード更新後はpausedになるため確認後にresumeする。アプリの通常CDでrootのupdaterコードをリモート置換する仕組みは設けない。
