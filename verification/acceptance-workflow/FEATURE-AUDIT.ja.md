# 受入開始に向けた機能棚卸し（2026-10-17）

比較基点: `5d649f8d8d684271e7e17eec0596e074e471696c`（PR #79統合後）。この文書は実装と開発側の確認範囲を記録し、ユーザー受入の完了を宣言するものではありません。

## 実装済み領域

| 領域 | 主な実装／テスト入口 |
|---|---|
| 初期設定・招待登録・Nativeパスキー認証 | Join、Identity、Sem.ApplicationJoinChecks / Sem.ApplicationLoginHttpChecks、e2e.cjs |
| 招待依頼・判断コメント・本人リンク・失効 | InvitationManagement、Sem.InvitationManagementChecks / Sem.InvitationIssuanceChecks |
| 本人パスキー・追加・rename・短期proof削除 | ProfileSettings、Sem.AccountPasskeyChecks / Sem.PasskeyProofChecks |
| 復旧・Cookie更新時のsession保持 | PasskeyRecovery、Sem.LiveSessionRenewalChecks（null principalも拒否） |
| 管理者アカウント管理 | Workspace、Sem.CoreChecks、admin-accounts-checks.cjs |
| イベント・フェーズ・パート定義・曲・設問 | Core各サービス／専用Checks、一般利用者との認可分離 |
| 時刻表・参加matrix・設問回答・観覧・管理者例外 | Timetable / Participation、Sem.EventMatrixChecks、event-matrix-checks.cjs |
| コメント・公開回答・進行・短縮・受付／QR | 各既存CoreサービスとChecks、comment-checks.cjs |
| プロフィール・メール変更・QR・通知 | ProfileSettings、各HTTP/UIスイート |
| 更新・SQLite／鍵／私有ファイル保全 | StorageChecks / LegacyResetChecks / AcceptanceRetentionChecks、全ディレクトリ復元fixture |

## 今回埋めた既知の差分

- イベント左側に本文コメントを統合し、参加送信とは独立した「コメントのみ投稿」ダイアログを追加。専用ルートも維持。
- 管理者ツールを左側の折りたたみ領域へ移動。フォームと認可・業務処理は再利用。
- matrixと時刻表の同日表示を時刻主体に整理。日跨ぎは日付を明示し、編集／計算の元契約は変更しない。
- プロフィール／アカウントのメニューを実設定の該当セクションへ接続し、画面内導線も整理。
- 管理者権限の結果不明時に「保存結果を再確認」を追加。下書きを捨てて読取だけ実行し、自動POST再送しない。モーダル破棄時のJS参照も解放。
- 実装済み機能を「後続実装」と案内していた古い説明を更新。
- データ入りDBの再起動・停止バックアップ・別ディレクトリ復元を.NETと実ブラウザーの両方で検証できるよう追加。CIに独立した受入ブラウザーjobを追加。

## 受入と切り分ける事項

- `main` からのDXP展開、公開HTTPS／proxy、物理認証器、本人宛メール／Push、協力者との操作は環境所有者による具体的な検証対象。
- UIのピクセル完全一致、デモのlocalStorageやデモユーザー切替の移植は受入開始の要件に含めない。
- 実Slotsにない未使用パートは列に追加しない。時刻表にない曲の枠は既存テーブルへfallback。
- 共有Event RowVersionの競合契約は維持。兄弟画面保存で下書きを勝手に消去せず、古い送信は競合で拒否し、明示再取得・再選択を要求する。
- INVの非NULL重複RequestId検査はデータを保持した更新の前提。自動データ修復はしない。

検証結果は同じディレクトリの結果文書および対象PRのCIに対応付ける。過去stage8の成功を今回の差分の成功として流用しない。

## 追加棚卸しで確認した未完了の操作差分

- 登録済み枠からのコンパクトなプロフィールは今回追加。既存の公開プロフィールと制限付きストリーム読取を再利用し、履歴は詳細画面だけに表示する。解除モードの選択と混同しない。
- 固定項目の外部言語JSONとヘルプ表示、共通の成功トースト／失敗ダイアログ、管理者の増分検索と監査の見せ方、対象表の並び替え状態表示は後続タスク。既存の業務操作・インライン結果・認可は利用できるが、モックの操作表現まで完了したとは扱わない。
- これらはNotionのAI担当実行タスクとして追跡する。ユーザーへ漠然とした使い勝手の評価を依頼して代替しない。
