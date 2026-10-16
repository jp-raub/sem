# アプリケーションのパスキーログインHTTP境界

この文書は、既存のログイン公開契約と、HTTP入力境界で行う厳格な検証を記録します。認証ポリシー、IdentityのWebAuthn動作、データベーススキーマ、グローバルなレート制限は変更しません。

## 既存の公開契約

- `GET /api/csrf` は引き続きantiforgeryトークンを返します。
- `POST /api/login-options` は `{"email":"…"}` のみを受け付け、ASP.NET Core Identityのネイティブなパスキー要求オプションJSONをそのまま返します。
- `POST /api/login` は `{"credential":"…"}` のみを受け付け、成功時に `{"message":"ログインしました。"}` を返します。
- どちらもクエリ文字列なしのPOSTです。設定済みの `Origin` と、`Content-Type: application/json`（任意で `charset=utf-8`）、`X-CSRF-TOKEN` ヘッダーおよびantiforgery Cookieを必須とします。フォームからのトークン代替は認めません。
- ログインオプションは、有効かつメール確認済みのアカウントに対してのみ生成します。認証には引き続き `SignInManager.PasskeySignInAsync` を使用します。通常のアプリケーションCookieは非永続で、既存のランダムな `sem.authentication-session` クレームを付与します。

## リクエスト検証

早期ログインルーターは、antiforgery検証またはJSON読み込みより前に、HTTPメソッド、クエリ文字列、宣言された本文サイズ、Content-Type／Content-Encoding、Origin、antiforgeryヘッダーの必須性を検証します。匿名ログイン要求のため、これらの検証より前に認証を要求しません。

- `login-options`：本文は最大4KiBです。
- `login`：本文は最大64KiBです。`credential` の上限50,000文字も維持します。
- 宣言されたContent-Lengthと、ストリーミング／chunked本文の両方にサイズ上限を適用します。
- JSONはBOMなしの厳密なUTF-8で、期待する名前のプロパティを1つだけ持つオブジェクトでなければなりません。最大深度は4です。重複・未知・型違い・空・上限超過・制御文字を含む値は拒否します。
- フォーム形式、圧縮された本文、クエリ文字列は拒否します。これらの検証前に汎用エンドポイント処理が本文を読み取ることはありません。

不正な要求ではIdentityのパスキー処理を呼び出さず、認証Cookie／セッションクレームも発行しません。ネイティブなパスキーオプション生成とサインイン検証はIdentityに委ね、パスワード認証へのフォールバックや新しい認証ポリシーは導入しません。