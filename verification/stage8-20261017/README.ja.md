# stage8 ローカル最終検証 — 2026-10-17

## 結果
- 凍結した同一候補で全75 .NETプロジェクトを直列build/runし、75/75成功。
- 合成UI検査9本を再実行し、9/9成功。
- 新しい合成DBからの単一nativeブラウザー通し試験は570検査成功、client errors 0。
- native試験と全回帰試験の後に対象815ソースのSHA-256を照合し、変更0。
- 実画面のデスクトップ／390pxモバイル画像を収録。実パスキー処理はCDP仮想認証器で検証。

## 今回の実装修正
追加キーの作成オプション応答にJSON Content-Typeがなく、UIが正常応答を拒否する不具合を修正。新旧ルートでHTTP回帰検証した。
Identityの既定30分SecurityStamp再検証で独自session claimが失われる不具合を再現し、同一本人・同一stamp・単一sessionの場合だけ新principalへsession bindingを引き継ぐ修正を追加。任意の旧claimや権限はコピーせず、検証間隔や失効条件は緩めていない。実Cookieの31分経過・失効・本人/stamp不一致等の専用13検査も成功。

## 来歴・未完了
GitHub main基点 2e9fe2bfd7567939e24cf3435ec84c5e4033cc22、固定mock 9cf8b031258fa1c9afe9668f54e61f002c4bce17。877ファイルのソースアーカイブは未公開・未コミット候補。復旧された記録と修復候補の混在であり、過去freezeとのbyte-identical復旧とは主張しない。
モック完全一致ではない。管理者操作欄の配置・大きさ、表内の日時表示、コメント本文の別ルート、プロフィール/アカウント導線等は追加調整が必要。パート列は実際のSlotsから導出し、未使用定義は表示しない。

## 安全範囲
すべて隔離されたローカル合成DB・loopback SMTP・仮想認証器で実行。実メール/Push配送、物理認証器、公開HTTPS/proxy、Docker、DXP、ユーザー実DBの受入は未実施。GitHub書込・PR・merge・配備なし。ユーザー実DBのreset疑義は未解決で、この合成試験では断定できない。INVの非NULL RequestId重複件数ゼロ確認は実配備前の必須ゲートとして残る。
テスト用host/SMTPは検証後に停止。ソースと証跡は保全した。

## 共有物の秘匿方針
本証跡は集計結果・ソースhash・非公開ログのhash・合成画面画像のみ。DB、メール、鍵、証明書、Cookie、仮想認証器credential、proof、rawログ、認証fingerprintを含み得る全チェック名は含めない。
