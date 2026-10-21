# /auth の送信元診断

## 観測と切り分け

iPhone Safariで `/auth` は「この送信元からの操作は許可されていません」、同じ端末・キーの `/` は成功するとの報告があります。この文言は `ApplicationLoginEndpoints` のOrigin拒否だけから返り、CSRF検証・Identityによるパスキー検証・セッション発行より前です。端末で実際に届いたヘッダー値は未観測であり、Origin欠落と断定しません。

`/auth` は明示的なfetch `same-origin`、旧 `/` は既定の `cors` でした。アプリ全体は `Referrer-Policy: no-referrer` です。非cors POSTとreferrer policyの組合せではOriginがnullとなる場合があります（[MDN Origin](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Origin)）。互換修正は `/auth` のfetchを `cors` に揃えるものです。接続先の固定アクション一覧・同一origin照合・相対pathname・credentials same-origin・redirect error・CSRF・サーバーOrigin完全一致は維持します。CORS許可ヘッダー追加、Origin欠落/nullの受理、認証リセットは行いません。

ローカルChromiumの合成no-referrerページでは両modeともOriginが送られ、Safariの不具合は再現していません。修正コードと物理Safariでの解消は区別します。

## Dockerで確認できる診断

`Sem.LoginBoundary` のInformationログに固定の `SEM_LOGIN_BOUNDARY` 行を出します。標準のConsole providerを使うDockerでは `docker logs` で確認できます。アプリのInformationを独自に無効化している配置ではその設定の確認が必要です。

- `action`：login-options / login の固定2値
- `stage`：origin-rejected / csrf-rejected / request-boundary-passed / options-issued / credential-rejected / session-issued
- Origin拒否の `reason`：missing / multiple / opaque / mismatch の分類だけ
- `diagnostic`：サーバーが要求ごとに生成する32桁ID。同じ値を `X-SEM-Login-Diagnostic` 応答ヘッダーに付けます。

生Origin、Referer、User-Agent、IP、メール、ユーザーID、鍵ID、challenge、署名、credential、CSRF、Cookie、本文、例外文は記録しません。ヘッダー値をそのまま診断へ流しません。通常の他ログを全文共有する必要はありません。

例：SEMのコンテナ名を現在のものへ置き換え、再現直後に次の行だけを確認します。

```bash
sudo docker logs --since 5m --timestamps <SEMコンテナ名> 2>&1 | grep 'SEM_LOGIN_BOUNDARY' | tail -20
```

`login-options options-issued` の後に `login origin-rejected` なら、選択後の送信でOrigin検査に拒否されたと分かります。`login session-issued` はアプリのセッション発行までの完了であり、その後の物理端末画面や他機能全体の受入を意味しません。該当行がなければ、アプリ前段の拒否・スクリプト未実行等も候補とし、Origin設定変更を推測で行いません。

## 検証方針

集中HTTP回帰でmissing/null/foreign Originの403・Cookie非発行、CSRF必須、サイズ・JSON境界と診断の秘密値除外を確認します。合成UIではブラウザー生成Originを手動設定せず、固定接続先・cors・同一origin credentials・redirect拒否を確認します。公開候補の全.NET・native・全データディレクトリ復元を維持します。

実機SafariではCDの対象版反映後に `/auth` の明示ログインを1回確認し、まだ失敗すれば上記の分類行だけで次の段階を絞ります。既存ログインをリセットしたり、秘密情報を共有したりする必要はありません。最終のCI/CD結果はPRの検証記録を参照してください。
