# DXP実認証・通知テスト版

.NET 10 Release / linux amd64 Docker / 空DB・再起動 / 仮想パスキー・SMTP・招待・予約通知の18項目を検証済みです。
検証: https://github.com/jp-raub/sem/actions/runs/38169405959

対象は初期設定、メール確認、パスキー登録・ログイン、招待・権限、簡易イベント参加、通知経路の外部テストです。モックv33の全UI・全業務機能の移植済み完成版ではありません。

配置手順: deploy/README.ja.md
データ・秘密情報は同梱しません。DXP配置、NPM/DNSの適用、実SMTPアカウント、Apple実機とPush事業者を通す確認は配置後の作業です。

GitHub Actionsのsem-dxp-delivery成果物にはソースZIP、Dockerイメージ、チェックサムが含まれます。配布用イメージはテストがすべて成功した場合にのみ出力します。
