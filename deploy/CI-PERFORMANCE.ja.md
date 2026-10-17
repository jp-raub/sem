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

全.NET suite、合成UI10本、対話browser109検査とデータ入り復元、Dockerの非root権限・初回起動・再起動は残します。ブラウザー版更新、テスト削除、失敗無視、無条件skip、実DBの公開は高速化として採用しません。
