# 本人向けパスキー管理

英語版を正本とします: [Account passkey management](PASSKEY-ACCOUNT-MANAGEMENT.md)。

この候補では `/profile/settings` にパスキー一覧・名前変更・削除と、既存のメール確認付き追加フローを統合します。認証メールは読み取り専用で表示します。`/Account` は追加・再公開せず、引き続き利用不可（404）です。

## 本人確認とリクエスト境界

- 専用の `/api/account-passkeys/{action}` が本人向け操作を扱います。変更要求には設定済みの公開 `Origin` との完全一致、既存の antiforgery token、有効な認証 actor が必要です。アカウントが有効・メール確認済みで、SecurityStamp claim が最新、認証セッション claim が未失効であることを確認し、要求中の expected actor も本人IDと照合します。
- 変更本文は最大64 KiB、厳密なUTF-8 `application/json`、単一JSONオブジェクトに限定し、重複・未知の項目を拒否します。URL queryへの入力値を許可しません。応答は `no-store`、リダイレクト応答は拒否します。旧アカウント追加route名は同じserviceへのadapterです。認証済みの旧options/completeも同じ追加flowを通し、匿名の招待・初回登録は従来handlerに残します。
- Assertion/登録検証にはASP.NET Core IdentityのネイティブWebAuthn処理を使い、独自のWebAuthn暗号検証は追加しません。credential IDやproofをナビゲーションURL、ブラウザーstorage、監査テキストへ書き込みません。

旧入力の例外：外部テスト画面は互換性のため、既存の `passkeyAddToken` queryリンクを引き続き受け付けます。最初の非同期API要求より前にqueryから値を除去し、ログイン中の場合に限り、保護された追加adapterへPOST本文で送ります。新規のリンク生成経路ではなく、新たなメールはURLではなく確認コードを記載します。ただし最初のページ要求には旧query tokenが含まれ得るため、クライアント側の除去で初回要求や上流ログまで消せるわけではありません。adapterは同じ有効な本人/session/stamp・一回性・期限を検査します。旧リンク対応の廃止は未実施です。上記の新規URL生成禁止を「旧queryリンクを一切受信しない」という意味には解釈しないでください。

## 名前変更と削除

一覧と名前変更は有効な本人sessionで行います。削除は用途を限定した別の確認手順です。選択したキーだけに限定したoptionsを取得し、そのキー自身のネイティブassertionで再認証したうえで、明示的に削除を確認します。assertion用challengeと、確認後の一回限りの削除proofはそれぞれ5分で失効します。後者は本人・現在の認証session・現在のSecurityStamp・対象credentialに結び付けます。追加や復旧のproofを流用できません。確認前・確認後いずれの有効proofも取消しで消費し、期限切れ・消費済み・本人/session/stamp/対象不一致は拒否します。

最後の有効パスキーは、削除と同じserializable database transaction内で最新件数を調べて阻止します。追加完了とcredential復旧もserializable transactionを使うため、最後のキー判定をすり抜ける競合を防ぐ設計です。同時変更や古い状態を成功とみなさず、再取得を求めます。

## 追加

既存の追加serviceとIdentity WebAuthn flowを再利用します。既存キーで再認証後、保存済みメールへ一回限りの64桁hex確認コードを送り、この設定画面で入力します（URLには載せません）。追加操作全体の期限は再認証から15分です。メール確認後の登録期限は5分または全体期限の早い方までです。再送は旧コードを無効化しますが、いずれの期限も延長しません。tokenはhashで保存し、送信待ちメール本文は既存通知serviceの保護を利用します。

## schemaと配置状況

`20261017000000_AddPasskeyDeletionProof` はproofテーブル、アカウント外部キー、state/time/hash制約、および有効proof向けindexを追加します。DbContext modelとsnapshotにも同じ候補schemaを反映しています。DXPへの配置や実databaseへの適用は未実施です。検証時には使い捨ての合成テストDBでmigrationを試験します。配置前に親の全変更と合わせてreviewし、databaseのコピーでmigrationを検証してください。

## 検証状況

初期修復はsource-onlyで作成しました。その後、親側でbuild・focused .NET tests・static UI checks・合成モデル検査を実行しています。正確な結果は各検証で記録されたソースmanifestだけに適用されます。対話ブラウザー受入、Dockerイメージ検証、物理端末、実配送、DXP配置は、それらから推測せず別に報告します。