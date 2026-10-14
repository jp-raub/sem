# SMTP認証IDとメール送信元の設定

英語正本は [SMTP.md](SMTP.md)。SMTP認証とメール送信元は別々に設定します。

| 設定 | 用途 |
| --- | --- |
| SMTP_USERNAME | SMTPサーバーへログインする認証ID。送信元メールアドレスと一致する必要はなく、メール形式でなくてもよい |
| SMTP_PASSWORD | その認証IDのパスワード。秘密情報として管理する |
| SMTP_FROM | メールのFromヘッダーに設定する送信元アドレス |

今回の送信元は `sem-app-notification@s.raub.jp`。今後は既存の非公開 `.env` のSMTP_FROMを変更してサービスを再作成すれば変更できます。既存の `.env` を `.env.example` で上書きしないでください。

```dotenv
SMTP_FROM=sem-app-notification@s.raub.jp
```

ComposeはSMTP_FROMをSmtp__Fromへ渡します。互換性のため、`${SMTP_FROM:-misc@raub.jp}` は未設定・空の場合に旧送信元へフォールバックします。アプリは認証IDと送信元の一致を要求しません。ただしSMTPサーバー側で送信元が許可されている必要があり、サーバー独自の制限や書き換えは別です。必要に応じ、送信ドメインのSPF／DKIM／DMARCをメールサービス側で設定してください。

## 既存DXP環境へ反映する

古い配布ZIPから配置した環境では、手元の `deploy/compose.yaml` の環境変数1行だけを次に変更します。

```yaml
      Smtp__From: ${SMTP_FROM:-misc@raub.jp}
```

既存 `.env` にSMTP_FROMを追加／更新します。SMTP_USERNAMEとSMTP_PASSWORDは新SMTPサーバーが発行した認証情報のままにします。他の設定、特に公開URL・信頼するプロキシ・データボリューム・Push鍵は変更しません。

```sh
cd /volume2/Docker/sem-dxp-delivery/deploy
docker compose -p sem-external-test --env-file .env \
  -f compose.yaml up -d --no-deps --force-recreate --no-build --pull never sem
docker compose -p sem-external-test --env-file .env -f compose.yaml ps sem
```

短時間停止しますがデータボリュームは削除しません。restartだけでは環境変数の変更が反映されません。アプリは既にSmtp:UsernameとSmtp:Fromを別々に読むため、このCompose変更にイメージ再ビルドは不要です。通常操作でrestore-check.override.yamlを使わず、down -vも実行しません。

全体管理者で通知管理の「自分へテストメール」を押し、実際の受信とFromアドレスを確認します。可能なら新SMTP側のログで経路も確認してください。送信待ち／処理済み表示だけでは受信やSMTPサーバーがFromを書き換えないことの証明にはなりません。

変更対象はメールのみです。Push__Subject（VAPIDの連絡先）とVAPID鍵は変更しません。`.env`、展開済みCompose設定、SMTP資格情報をチャットやGitに貼らないでください。
