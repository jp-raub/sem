# DXP向け取得型CD

## 採用方針と現在の到達点

環境所有者の指示に基づき、**mainの検証成功後にDockerイメージを公開し、DXPが外向きに取得して自動更新する**方式を採用する。最終形はワンクリックのみではなく、一時停止・再開付きの継続的デプロイメント。GitHub mainはコードの正本であり、配備はそのcommitから生成・検査されたdigest固定イメージを使用する。

**この変更は公開側の第1工程。DXPの自動更新はまだ有効にならない。** 取得・更新サービス、初回導入、現地受入は後続工程。現時点では既存のDXPで何も設定・変更する必要はない。

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

## DXP側の実装契約（この工程では未実装）

- root管理の固定プログラムと非公開設定を、systemd timer等で定期実行する。PR Runnerや外部SSH着信は不要。DXPのsystemd対応・既存配置の実態は初回導入前に確認する。
- 既存Compose project／service、/dataマウント、環境変数、NPM、URLを維持する。Compose全体や他サービスは更新しない。
- 排他lockを取り、停止中／失敗保留中は新候補を適用しない。新しい合格候補は最新にまとめ、二重更新しない。
- pullとイメージのrevision／architecture／non-root検査を停止前に行う。
- SEMを停止し、SQLite・WAL・鍵・画像を含む/data全体、秘密設定、旧イメージを非公開バックアップとして検証する。容量不足や保全失敗なら切替しない。
- `--no-build --no-deps`相当でSEMだけ再生成する。`down -v`、データ初期化、認証リセットは実装しない。正しい永続volumeかを起動前後で検証する。
- 起動後はlive／readyを連続確認し、稼働commit・digest・結果・保全先を非秘密statusへ記録する。公開HTTPSとユーザーの機能受入は別に扱う。
- 新版起動後の失敗は失敗状態を残して再適用ループを止める。旧イメージ単独へ自動downgradeしない。対応するデータ・鍵・設定・イメージを一組として復旧する。
- 検査済みの追加的migrationは保全後に自動適用する。破壊的な変更など特別な更新だけ明示的保留とし、全migrationを一律手動待ちにしない。
- pause／resume／statusを初回から用意する。失敗の解除は単なるresumeと区別する。保全物・GHCR・古いイメージの保持を無制限にせず、ただし自動削除の範囲と最小保持数は検証して別途実装する。

Dockerを操作する権限はホスト管理相当になり得る。更新サービスをコンテナ化してDocker socketを渡すだけでは、その権限がSEMに限定されるわけではない。root所有の固定設定／コードで対象を制限し、候補から任意のComposeパスやコマンドを受け付けない。設定値やバックアップをログ・GitHubへ出さない。

## 初回設定の役割

AIはコード、ローカル・GitHub検証、main統合、対象版を固定した導入／停止／再開／復旧手順を用意する。環境所有者はDXP側の初回設定と、具体的に依頼された端末確認を行う。その後の毎回のpull／ビルド／再起動／ログ転記をユーザー作業にしない。

GHCR packageは初回にprivateで作成される。今回の配布物は既存公開ソース由来で秘密を含めないため、匿名pullできるpublic packageを推奨する。必要な最初の可視性設定は初回導入手順に含める。privateのまま運用する場合のみDXPにread:packagesの認証が必要。秘密はNotionやチャットで受け取らず、DXP上で設定する。未確認のpublic化や認証設定を済みと扱わない。

## 無料での運用と実行時間

- mainの既存CI成功を再利用し、CDで全.NET／browserを再実行しない。API照合とDocker固有検査だけを追加する。
- イメージ公開はmain成功時のみ。PRの公開権限、配備Runner、NAS向け公開ポート、有料サービスは不要。
- 古いmainのrunはgateで除外する。公開処理は直列で、途中の公開をcancelしない。
- GitHub公式説明ではpublic package利用は無料、GHCRのcontainer storage／bandwidthも現時点で無料とされる。ただし将来の保証ではない。Actionsの料金・制限はリポジトリ可視性・runner種別・契約による。追加の有料runner・課金設定は行わない。
- `candidate.json`以外の実行ログ／DB／鍵／メール／Cookie／credentialをrelease assetにしない。

公式資料（確認時点の情報）:
- https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry
- https://docs.github.com/en/billing/concepts/product-billing/github-packages

## 検証と制限

`python3 scripts/cd/release_gate_checks.py`はネットワーク・Docker・秘密不要の契約試験。`bash -n scripts/cd/image-smoke.sh`で構文確認する。PRの`image-validation`はGitHubのLinux runnerで実際のDockerを検証する。

DockerのないAIローカル環境でDocker動作やGHCR公開の成功を主張しない。GitHubのCI結果、main公開結果、DXP更新、ユーザー受入を段階別に追跡する。本書だけで自動配備が稼働するわけではない。
