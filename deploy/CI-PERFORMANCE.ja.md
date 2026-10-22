# GitHub Actionsの無料運用と検証時間

## 課金枠とタイムアウトを区別する

このリポジトリはpublicで、標準のGitHub-hosted `ubuntu-latest` runnerを使います。公式説明ではpublicリポジトリの標準runner利用は無料で、privateリポジトリ向けの月間無料minutesとは別です。有料large runnerやself-hosted機器の導入は行いません。公開設定・プラン・GitHub規約が変われば再確認してください。

- GitHub-hostedの1job上限：6時間。このworkflowはさらに40分／25分で停止します。
- 成果物とcacheには別途容量・保持制限があります。現在の結果保持は7日。依存cacheは各jobのNuGet packageのみで、DB・Cookie・鍵・private画像・ブラウザープロファイル・build成果物を含めません。
- `concurrency` と `cancel-in-progress: true` は既存のまま、同じPR/refの古いrunを中止します。mainとPRの検証はそれぞれのtreeを検証するため維持します。

公式資料：[Billing and usage](https://docs.github.com/en/actions/learn-github-actions/usage-limits-billing-and-administration)、[Actions limits](https://docs.github.com/en/actions/reference/limits)。

## 重複ビルドを除く

従来は76個のsuiteごとに`dotnet build`を行い、共有するSem.Webのrestore/build評価も繰り返していました。

`scripts/build-integration-projects.sh`は、その時点のアプリと**全**`tests/*/*.csproj`から一時solutionを作り、完全な依存graphをrestoreして一括buildします。build並列度は2に制限します。一時solutionは成功時も失敗時も削除します。restore/buildの失敗はそのままCI失敗です。

suiteの実行は引き続き逐次です。`--no-build --no-restore`で直前のbuildを使い、従来のnetwork namespace・loopback・権限削減を維持します。fixtureのポート／DB競合を増やすためのテスト並列化はしていません。NuGet監査も無効化しません。

## package cache

各jobは`actions/cache`でNuGet packageを再利用します。OS・SDK系列・project/props/targets等の依存入力とjob用途をキーにします。cacheがなくても通常restoreするためcold cacheで動作し、cache機構の失敗だけで検証を止めません。cache hitによるテストskipや古いbin/objの再利用はありません。固定cacheサイズの追加購入は行いません。

## 実測と維持する検査

比較元はrun [39030792356](https://github.com/jp-raub/sem/actions/runs/39030792356)：integration-checks **13分47秒**、acceptance-browser **9分23秒**。二つは並列であり、合計時間がそのまま待ち時間ではありません。

改善後は`verification/timings.tsv`にUI・全graph restore/build・各suite・全suite・publish/Docker build・Docker smokeの秒数を記録します。初回のcold cacheと後続cache hitは区別します。結果は変更PRの実測コメントとActionsの実行履歴に記録し、推定短縮率を成功実績として扱いません。

上記の合成UI10本・browser109検査は初回高速化当時の数で、現在の固定件数や上限ではありません。現行の全.NET suite・合成UI・対話ブラウザー・データ入り復元、Dockerの非root権限・初回起動・再起動を維持します。ブラウザー版更新、テスト削除、失敗無視、無条件skip、実DBの公開は高速化として採用しません。

## CI・受入テスト効率化の試行（2026-10-21開始、暫定）

次の5件のPR、または2週間（2026-11-04 UTCまで）の早い方で中間評価します。PR数が不足する場合は勝手に延長せず、評価材料と継続期間を相談します。起点はこの計測導入PRです。過去PR101は参考例で、5件へ遡及計上しません。

### 保持する品質境界

- 受入済みの再現可能な動作は合成データの自動回帰で保持します。手元への依頼は新規・変更箇所とDXP／物理端末などの非自動化部分に限定し、未変更機能の一式再確認を毎回求めません。
- SQLite/WAL、Data Protection鍵、私有ファイルを含む保全検証を削除しません。DB・migration・鍵・保存ファイル・バックアップ／復元・CD／更新に影響する変更、および**公開候補**ではフルの一式保全・復元・利用可能性確認を必須にします。影響が明確にない開発途中では関連回帰と軽量保持確認を選べますが、不明ならフルへ戻します。
- 今回は計測のみを導入し、PRとmainの既存フル検証・25分／40分上限・CDの両必須jobを維持します。条件skip、失敗無視、assertの削減は追加しません。
- 並列化はフェーズ実測を得てから、独立した一時環境・データ・ポートで実行できる責務に限定して検討します。同じDB／ポートを共有した並列化や、時間短縮の推測値を実績とは扱いません。

### 計測ファイル

- `verification/acceptance-native/fixture-phases.tsv`：publish、fixture起動、ブラウザー全体。
- `verification/acceptance-native/acceptance-phases.json`：初期登録、最初の全ディレクトリ復元、各実行ヘルパーの所要時間とsuccess/failure/running。中断時のrunningは成功ではありません。失敗は元の例外のまま伝播します。
- 結果に記録するのは固定フェーズ名、時間、状態だけです。URL・宛先・actor・Cookie・DB・鍵・要求本文・例外内容は追加しません。Actionsの公開artifactは従来結果JSONと上記2ファイルだけの許可リストです。同じ固定フェーズ名・時間・状態をActionsのnoticeにも出し、非秘密の計測結果を取得できるようにします。

### PRごとの時間・品質記録

基準は[完了run #198](https://github.com/jp-raub/sem/actions/runs/39392219780)：critical path 22分19秒、integration 13分53秒、browser 22分19秒。単一標本であり中央値や恒常値ではありません。生のAPI応答ではなく[非秘密の集計](verification/ci-policy-baseline-20261021.json)を保存しました。

`scripts/ci-run-metrics.py --run run.json --jobs jobs.json --output summary.json` はActionsの公開run／完全なjobs一覧JSONから、初回jobまでの待機、critical path、最初の完了jobまで、全job合計、attemptを計算します。最初の完了jobは有用な結果までの時間の**代理値**であり、最初のassertの時刻とは混同しません。job合計とcritical pathも別です。未完了・別run混在・ページ省略・時刻逆転は拒否します。

各対象PRの最終コメントには、対象headとPR/mainのrun・attempt・計測結果に加え、以下を記録します。

1. 再試行と不安定失敗（未評価は0件と記録しない）
2. CIが検出した不具合と診断。失敗したjob数を製品不具合数と断定しない
3. ユーザー受入で見つかったデグレ（未受入は未評価）
4. データ保全／復元／利用可能性の結果
5. 手元確認の具体的な依頼数と負担。計測だけの今回の変更には新しい手元操作を依頼しない

品質低下が見られた責務は広い検証へ戻します。計測の導入自体を速度向上や次の並列化の成功として数えません。


## PR106のブラウザー準備時間と中断対策

初回run 39774267411 attempt1はintegration-checks成功、acceptance-browserは30分で中断した。公開job情報ではブラウザー依存インストールが11:56:04〜12:06:01（597秒）、full fixtureのstepが12:06:07〜12:25:54（1187秒）。10フェーズの成功通知だけでfull成功や中断箇所の原因を断定しない。既存1MiB画像のfocused fixtureはローカルで40検査・初回一式復元が成功したが、fullの状態依存を否定する証拠にはしない。

ブラウザーは既定のheadless launchを使い、channel指定やheadless=falseはない。Playwright 1.56.1は維持し、`install --with-deps chromium --only-shell` で使用するheadless shellと必要なsystem依存を取得する。未使用の通常版Chromiumを取得せず、バージョン・OS・architecture別の依存バイナリだけをrunner.temp配下へcacheする。毎回installを実行し、cache hitでも検査を省略しない。DB・鍵・Cookie・ブラウザーの利用者profile・アプリbuildをcacheしない。

両ブラウザーcontextに30秒の既定待機上限を設定し、従来無期限だったwaitForFunction等は条件未達なら失敗させる。assertionを弱めず、元の例外とphase failureを維持する。CIの中断時も既存の非秘密な失敗位置要約を実行対象にする。jobの30分上限、full fixtureの全flag、2回の一式復元、公開artifact許可リストは維持する。

これは依存取得と失敗診断の改善であり、実測前に高速化達成とは扱わない。cold cache・system依存の時間やrunner差は引き続き計測する。再実行の最終headでPR/mainのfull CIを確認するまで公開完了にはしない。
