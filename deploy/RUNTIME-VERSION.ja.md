# 公開URLによる稼働ビルド確認

## 表示と意味

- `/about`：匿名で読める「応答しているアプリの稼働版」。完全なcommitとビルドID、固定commitへのソースリンクを表示する。
- 共通フッター：短縮commitとビルドID、`/about`への通常遷移リンク。トップページからも参照できる。長い識別子は狭幅で折り返す。
- `/api/version`：匿名GETのJSON。schema=1、application=SEM、channel、commit、buildId、publicationRun、publicationAttempt、probeだけ。日時・環境・内部パス・利用者情報・鍵は含めない。
- `/health/ready`：既存のDB接続可能性を確認する。任意のprobeを返し、no-storeにする。全業務の成功を表すものではない。

正式ビルドは実行中の**製品アセンブリ**に固定した完全SHAと公開workflowのrun/attemptを返す。ビルドIDは `run-{publicationRun}-{publicationAttempt}`。環境変数やOCIラベル、GitHub mainの表示値で代用しない。同じcommitの再公開でも異なるビルドになる。欠落・不正な情報は開発版／nullとして扱い、正式版との照合には使えない。ビルドIDは適用完了日時ではない。

`dotnet Sem.Web.dll --build-info` はDB・HTTP・worker・設定読込より前に同じ情報をJSONで出力する。公開処理はネットワークなし・読み取り専用・非rootの**実際にpushする同一image**でこれを検証する。image digestの自己埋込みは行わず、digestは従来どおり取得側updaterのinspectで照合する。

## 自動監視

`CD public runtime observation` は `CD verified image` の**完了後**に動く別workflow。公開workflowの必須jobにしない。取得側は公開成功まで待つため、同じworkflow内で配備完了を待つと循環待ちになる。

1. 取得側の `GitHub.latest()` と同じ検証でcurrent mainの正式候補を確認する。候補のrelease/tag、integrationの両job、公開run/attempt、commit、image digest、platformを照合する。workflow_run起動では起点の公開run/attemptとも一致が必要。
2. 固定HTTPS origin `https://sem-test.raub.jp` の `/api/version?probe=...` と `/health/ready?probe=...` を確認する。OS標準のTLS検証を維持し、redirect・HTML200・200以外・巨大応答・重複JSONキー・no-store欠落は拒否する。GitHub tokenを公開URLへ送らない。
3. 毎回異なる予測不能probeの一致、厳密な型／schema、完全SHA・ビルドID・readinessを確認。20秒間隔で3回**連続**一致が必要。不一致や通信失敗では連続数をリセットする。取得timerの5分間隔等を考慮し観測は最大12分。
4. 最後に正式候補を再検証し、観測中のmain更新・候補変更では成功にしない。
5. Actions summaryと非秘密artifact `runtime-observation.json` へ記録。`runtime-match`は公開URLでの稼働ビルド一致。`unconfirmed`は反映未確認でworkflowは非成功。意図したCD一時停止、更新待ち、DNS／proxy／TLS／API障害等を区別できず、配備破損とは断定しない。

手動で再確認する場合は同じworkflowの `Run workflow` を使える。現在の正式候補のみを読み取る。公開・更新・pause/resumeは行わない。古いrunの結果はその候補の観測履歴であり、常時「現在最新版」と保証する表示ではない。

probeは任意の32桁小文字hexを1個だけ受け付ける。指定しなくても人はJSONを読めるが、自動監視はprobe必須。通常のhealth利用も従来どおりprobeなしで可能。未知のqueryや複数probeは400/no-store。

## 限界と安全条件

これはHTTPSで応答したビルドとDB接続可能性の観測であり、ホスト内全コンテナ・全レプリカ・全経路、image digestの外部証明、物理パスキー、実配送、全業務の受入ではない。反復確認でも負荷分散の全台網羅は証明しない。フッターはページ表示時点の情報なので、最新応答はno-storeの `/about` とprobe付きJSONで確認する。

データ・認可・CSRF・API要求形式・updaterの切替条件は変更しない。公開候補では既存の全データ一式バックアップ／復元を維持する。画像10MiBの保留、Safariの同一再試験不要を変更しない。
