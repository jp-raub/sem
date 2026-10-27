# 採用モックの画面基準と機能接続順

## 正本と現在地

- 要件の正本はNotion「要件・整合性・テスト観点」。この資料は**実装用の画面・接続対応表**で、要件を追加・変更しない。
- UI基準：`9cf8b031258fa1c9afe9668f54e61f002c4bce17` の `mock/`。`wwwroot` の171資産と読み込み順を保持する。
- 機能の参照元：保全済み旧main `812b2784212e9015ecbbce31280e48cb0bc65e84`。**旧Razor画面・CSS・描画は戻さない**。旧コードの存在は新mainへの移植完了を意味しない。
- 既定公開配置はUIデモ。明示実認証モードでは既存パスキー認証とDBの一覧・イベント詳細・プロフィール・本人アカウント・保存済み配色の参照を接続済み。メール、保存、参加登録、QR、通知は実接続前。`/about`、`/api/version` と公開版照合だけは実装済み。
- `app.js` 単体で判断しない。`management-navigation.js` → `invitation-approval.js` → `invitation-administration.js` を含む、`index.html` の全ラッパー適用後を基準にする。

## 固定画面一覧

機械可読の基準は `tests/fixtures/ui-route-baseline.json`。18経路 × 未ログイン／参加者／イベント限定管理者／全体管理者の72表示を記録する。動的IDの代表値は `e1`（募集中）、`e2`（非公開）、`e3`（閲覧）、`u1`（プロフィール）。モック内の権限別**表示**であり、製品APIの認可試験ではない。

|経路|画面・扱い|
|---|---|
|`#/`|未ログインの入口／イベント一覧、並べ替え、新規イベント入口|
|`#/login`|パスキーログインの模擬画面|
|`#/register?invite=SESSION-DEMO`|招待登録。人数・期限・失効の分岐は別状態|
|`#/account/enrollment`|メール確認→初回パスキー登録の模擬フロー|
|`#/account/recovery`|認証復旧の模擬フロー|
|`#/profile`|プロフィール管理、SNS・追加設問・登録履歴・プロフィールQR|
|`#/profiles/u1`|プロフィール閲覧（動的な本人／他者）|
|`#/account/manage`|アカウント管理の統合入口|
|`#/account/passkeys`|パスキー管理。旧入口も残る|
|`#/account/email`|メールアドレス変更|
|`#/account/notifications`|端末の通知設定。旧入口も残る|
|`#/notifications/manage`|通知管理。許可設定と通知事項の定義|
|`#/invitations/manage`|本人リクエスト／管理者許可・拒否・直接発行|
|`#/messages`|メッセージボックス|
|`#/admin/users`|サービス管理。全体管理者以外の拒否表示も固定|
|`#/events/e1`|募集中イベント、名簿、担当管理者のチェックイン・通知タブ|
|`#/events/e2`|非公開イベント。担当外の拒否／全体管理者の表示|
|`#/events/e3`|閲覧イベント。担当外では管理タブを出さない|

イベントはタブ切替でURLが変わらない。22個の表示タブについて選択、表示パネル、ARIA関連付けを実ブラウザーで検証する。モーダルも別URLではなく、次表で追跡する。

## 主要状態の接続チェックリスト

**列挙済み＝自動検証済み／実装済みではない。** 画面基準回帰が確認するのは上記72表示と22タブ、および代表4モーダル（プロフィール編集・招待リクエスト・直接発行・イベント作成）の表示／フォーカス／閉じるだけ。未記載の細部は固定モックの該当操作を確認して追加する。合成モックに存在しない通信待ち・通信失敗は、接続時に既存DOM内の状態として実装する（別UIへ作り替えない）。

|領域|同一画面で保持する主要状態・操作|主な固定モックの実装|
|---|---|---|
|共通|メニュー開閉、役割別項目、ヘルプ、必須／任意、狭幅、ライト／ダーク、サイト→イベント→個人テーマ|`site-ui.js`, `management-navigation.js`, `settings-refinement.js`|
|認証|利用可能／非対応／中断／失敗、招待不正・期限・人数・失効、メール確認待ち・再送・期限切れ、初回登録、復旧限定状態|`account-notifications.js`, `account-management.js`|
|一覧|募集中／閲覧／非公開、空一覧、題名／開催日時ソート、新規作成、プリセット選択|`app.js`, `definitions.js`, `event-opening.js`|
|イベント|タイムテーブル、占有枠の本人表示、空枠選択、兼務、上限、統合申込み、観覧、取消、部分成功・競合|`entry-refinement.js`, `attendance.js`, `event-rules.js`, `event-features.js`|
|編集|概要、曲／進行、秒単位、準備継承、並べ替え、フェーズ、パート、設問、管理者、下書き反映、欄だけ取消、全体保存／取消、理由必須削除|`event-refinement.js`, `settings-refinement.js`, `phase-refinement.js`, `preset-refinement.js`, `modal-overview.js`|
|進行|現在・次曲、開始／停止・補正、予実時刻、余裕時間、遅れ、操作取消、一般／管理者の表示差|`progress-refinement.js`, `progress-audience.js`, `time-comparison.js`, `live-buffer-proposal.js`|
|名簿・プロフィール|検索・並べ替え、公開回答、本人／他者、SNS、追加設問、未登録、プロフィール編集・画像即時保存と文面保存の分離、登録履歴、QR発行／更新／拡大|`profiles.js`, `completion.js`, `profile-history.js`, `ux-complete.js`|
|受付|QR発行・再発行・読取、読取と受付の区別、手動受付、本人確認チェック、受付メモ、完了／取消、履歴、無効・別イベントのQR|`event-operations.js`, `checkin-reception.js`|
|通知・受信箱|端末許可、非対応・拒否、購読・解除、テスト、イベントルール追加／編集／削除・有効化、対象・即時／日時／相対、未読／既読|`account-notifications.js`, `event-operations.js`, `invitation-approval.js`|
|招待|本人リクエスト、許可／拒否、期限・人数・方式、直接発行、リンク一覧、即時失効、理由・操作者の記録|`invitation-approval.js`, `invitation-administration.js`|
|サービス|権限変更、特権保護、認証リセット、イベント管理者、パートプリセット、プロフィール設問、テーマ定義の編集・保存／取消|`management.js`, `account-management.js`, `definitions.js`, `preset-refinement.js`|

## 機能接続の実施順と旧機能の再利用境界

依存順に小さく実装する。各段階で、接続した操作だけを実機能として扱い、未接続操作の模擬成功を混ぜない。認証完了前に実DBの書込みAPIをUIから使わせない。

|順|対象|旧mainの再利用候補（`src/Sem.Web/` 配下）|着手／完了の境界|
|---|---|---|---|
|1|認証・データ基盤と読み取りセッション|`Data/ApplicationDbContext.cs`, `Data/ApplicationUser.cs`, `Runtime/DeploymentSettings.cs`, `Runtime/LiveAuthenticationSession.cs`, `Runtime/ApplicationLoginEndpoints.cs`|既存DB・Identity・鍵とマイグレーションの互換を隔離コピーで検査してから接続。CSRF・Origin・本文上限・レート制限・失効確認を保持。デモのロール選択を実認証に使わない|
|2|招待登録・アカウントの実接続|`Runtime/ApplicationJoinService.cs`, `ApplicationJoinEndpoints.cs`, `AccountPasskeyService.cs`, `AccountPasskeyEndpoints.cs`, `PasskeyAdditionService.cs`, `EmailChangeService.cs`, `PasskeyRecoveryService.cs`|上記基盤に加え短期・一回限り・用途限定の証明、メール、直近再認証が必要。ログインだけを移植して追加・復旧の保護を落とさない|
|3|イベント・プロフィールの読取り→限定した保存|`Runtime/SemEndpoints.cs`, `Runtime/Models.cs`, `Data/ApplicationDbContext.cs`|旧の大きなエンドポイントから契約・ドメイン処理を分離し、固定モックの描画へDTOを接続。版競合・保存と取消・画像即時保存の区別を守る|
|4|申込み・管理編集・当日進行・QR受付|`Runtime/SemEndpoints.cs`, `Runtime/Models.cs` と対応エンティティ|サーバー側の権限・フェーズ・上限・競合・部分成功・監査が必要。QR読取を受付完了と混同せず、旧実装と最新要件の差を先に照合する|
|5|通知・招待管理・受信箱|`Runtime/DeviceNotificationService.cs`, `NotificationService.cs`, `InvitationManagementService.cs`, `InboxService.cs` と各Endpoints|業務確定分だけを通知。閲覧権限と本人分離、失効購読、再試行、招待の人数・期限を再検証。実Push端末到達は別証拠|
|6|残機能とリリース境界|全接続結果と要件正本の照合|全UI接続後にまとめて必要な限定受入。既存端末のメール入力なしログインは後続要求として実装し、現在のデモで満たしたとしない|

旧DB・鍵はこの台帳作成では読み書きしない。旧 `ApplicationLoginEndpoints` は `UserManager`／`SignInManager` に加え `PasskeyAdditionService` のセッションclaimと `SemEndpoints.Secret()` に依存する。単独コピーで認証基盤が復旧したことにはならない。

## 高速な検証

```sh
# dotnet publish とローカル起動後。PlaywrightのあるNODE_PATHを指定する。
python3 scripts/ui-inventory-checks.py
node scripts/ui-inventory-browser.cjs
```

- 画面基準回帰は既存の同じブラウザーCI jobで実行する。別の長いfull suiteや追加承認待ちは作らない。
- 毎変更：変更領域の集中的なドメイン／API検証＋固定モック資産一致＋必要な画面回帰。境界：認証・権限・整合性・復元をリスクに応じて確認する。
- 未ログインと3ロールは毎回新規ブラウザーコンテキスト。基準値の自動再生成、実個人情報入力、外部送信、公開サイトへの検証書込みはしない。
- 72表示の一致・22タブの到達性は見た目の全ピクセル一致、全モーダル・全組合せ、実認証、DB永続化、全端末の証明ではない。

## 保存基盤の分離段階
[保存基盤の分離と検証](../storage/FOUNDATION.ja.md)として、旧モデル・migration・初期化を独立ライブラリへ移植した。これは上表の接続順1の準備部分で、公開ホストの参照・実セッション読取り・実認証はまだ接続していない。UI資産と画面基準は変更していない。

## 認証の製品接続段階

[実認証モードと配置・保全・固定ログインDOM](../auth/PRODUCT-CONNECTION.ja.md)を追加した。既定は引き続きdemoで、明示配置時だけ既存アカウントの標準パスキー認証・最小本人セッション・ログアウトを接続する。最終mockと4幅×明暗の寸法・字体・配色を比較する。新規登録／復旧、保存済みテーマ、イベント以降のDTO・保存はまだ後続であり、デモの模擬成功は混ぜない。実volumeの接続・公開実認証の有効化は別の配置工程である。

## イベント一覧の接続段階

[一覧接続の契約と境界](EVENT-LIST-CONNECTION.ja.md)を追加した。明示実認証モードの一覧だけを保存データへ接続し、公開・担当非公開・現ロール・有効期間を読取り時に再判定する。参照はDBを変更せず、本文や他人の割当等を返さない。採用mockの一覧・3指標・列ソートを実装し、未接続の詳細・作成・保存を成功表示しない。招待／メール／限定権限を安易に単独移植せず、既存認証で安全に参照できるこの範囲を先に接続した。既定公開配置はdemoのままで、全UI完成や実volume有効化ではない。

## イベント詳細の参照接続

[詳細接続の契約と限界](EVENT-DETAIL-CONNECTION.ja.md)。固定mockの概要・予定表・現在名簿・コメント表示へ最小DTOを接続した。画像にも毎回同じ認可を適用する。登録・編集・投稿等の操作は無効または未接続案内とし、模擬成功を返さない。上の一覧接続記録にある「未接続の詳細」はその段階の履歴であり、現在は読み取りだけ接続済み。実volume有効化や全UI完成を意味しない。

## プロフィールの参照接続

[プロフィール接続の範囲と公開境界](PROFILE-READ-CONNECTION.ja.md)。固定mockの本人／メンバー／コンパクト表示と、閲覧権内の現在登録へ最小DTOを接続した。画像にも現認証・対象有効性を再確認する。プロフィール編集・画像更新・QR・未ログイン共有は未接続のままで、通常の個別承認依頼は作らない。既定公開配置はdemo、実volume未接触。

## 本人アカウント参照の接続段階

[アカウント参照契約](ACCOUNT-READ-CONNECTION.ja.md)として、採用account hub・旧キー入口・メール変更入口を独立アダプターへ接続する。本人の保存済みメールと標準Identityパスキーの名前・登録日時だけを返し、Credential ID・公開鍵・署名回数等は返さない。変更・キー追加／削除・メール送信は未接続で、画面だけで完了した表示へ進ませない。

## 公開回答集計の参照接続

[集計接続の公開境界と操作](PUBLIC-ANSWER-CONNECTION.ja.md)。イベント詳細の同一認可snapshotからYES／NOの割合帯・公開自由記述の人数棒・本文非公開の回答者数を接続する。固定mock最終グラフを保持し、要件U07の表確認は補助開閉で提供する。ホバー／実touch／キーボード・内部プロフィール遷移・外側閉鎖を扱い、公開解除・失効・失敗で古い表示を消す。設問編集・回答保存・保存直後更新は後続であり、参照接続だけで完了扱いにしない。

## 保存済み配色の参照適用

[配色の読取・初回適用契約](APPEARANCE-READ-CONNECTION.ja.md)。個人→対象イベント→サイト→既定の色とモードを独立に解決し、現在本人／対象へ束縛した最小DTOを固定mockの最終色計算へ接続する。初回の通常画面移動・端末モード変更を扱い、遅延・失効・不正色・取得失敗では前の私的適用を消す。選択／保存／プレビュー／取消／カタログ編集はこの参照接続だけで完了扱いにしない。
