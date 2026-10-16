# アプリケーションのパスキー認証UI

## 対象範囲

`/auth` は本番向けのパスキー専用ログイン画面です。`/` の外部診断・テスト画面（`Home.razor` と `sem.js`）とは分離します。パスワード、リカバリーコード、別のIDプロバイダーは追加しません。ユーザーが明示的にフォームを送信するまでWebAuthnを開始しません。SSR時のフォーム操作は無効にし、`auth.js` の初期化とCSRFトークン取得後にのみ有効にします。スクリプト欠落や初期化失敗時は、アクセシブルな状態・エラー表示で操作できないことを伝えます。

既存エンドポイントを変更せず、次の契約を利用します。

1. `GET /api/csrf` は `{ "token": "..." }` を返し、CSRF Cookieを設定します。
2. `{ "email": "..." }` を `POST /api/login-options` に送り、ネイティブの `PublicKeyCredentialRequestOptions` JSONを直接受け取ります。POSTには `X-CSRF-TOKEN` を付けます。
3. 明示的なクリック後にブラウザーで `navigator.credentials.get({ publicKey })` を呼び出し、アサーションをシリアライズします。
4. `{ "credential": "<シリアライズ済みアサーションJSON>" }` を `POST /api/login` に送り、検証します。成功応答の `message` を確認後、安全なローカル戻り先へ移動します。

両POSTはsame-origin fetch、Cookie、`cache: no-store`、`redirect: error`、25秒タイムアウト、上限付きUTF-8 JSON読取を使います。応答は最大1 MiB、fatal UTF-8でデコードし、JSON Content-Typeを必須とします。資格情報ペイロードはAPI契約の50,000文字以下に制限し、一時的なローカル変数にだけ保持します。メール、CSRFトークン、資格情報をURL・ブラウザー保存領域・ログに書き込みません。

## ナビゲーションとセッション安全性

未認証で保護ページにアクセスした場合、ローカルパスのみの `returnUrl` を付けて `/auth` に遷移します。既存リダイレクトコンポーネントは `forceLoad: true` を使い、Workspaceのログインリンクにも `data-enhance-nav="false"` を指定します。どちらもドキュメント全体を再読み込みします。`auth.js` は静的ページで一度だけ初期化され、BlazorのEnhanced Navigationでは再初期化されないためです。リダイレクトコンポーネントは元ページのクエリとフラグメントを意図的に破棄します。クライアントは戻り先を1つだけ受け取り、非ローカルパス、プロトコル相対URL、エンコード／通常表記のスラッシュ・バックスラッシュ・制御文字の細工、不正なエスケープ、認証情報、フラグメントを拒否し、パーセントデコードを上限付きで行ってから先頭セグメントを判定します。`/auth`、`/api`、`/Account` は通常表記・1回／複数回エンコードした別名も拒否し、same-originを確認します。最後にクエリとフラグメントを破棄し、承認したローカルpathnameのみを返します。不正値の既定値は `/workspace` です。これによりログインループ、APIへの遷移、オープンリダイレクト、元URLの秘密情報の持ち越しを避けます。

認証とセッション発行は既存サーバーエンドポイントが担います。サーバーはランダムなセッションclaimを含む非永続アプリケーションCookieを発行し、ライブセッション検証もサーバー側に残します。クライアントは認証セッションを保存・生成しません。

最終ログインPOSTの通信・プロトコル結果が不明な場合、フォームをロックし、POSTを自動再送しません。ユーザーが明示的に読み取り専用の `GET /api/status` で照合できます。認証済みユーザーが確認できれば安全な戻り先へ移動し、確認できなければロックしたまま時間を置いて手動再読み込みするよう案内します。

ページ破棄時は実行中のリクエスト／WebAuthn操作を中断し、世代番号を無効化してメモリー上のCSRFトークンを消去します。古い結果は無視します。条件付き・自動WebAuthn UIは使いません。

## 対象UIファイル

- `src/Sem.Web/Components/Pages/Auth.razor`
- `src/Sem.Web/Components/Account/Shared/RedirectToLogin.razor`（このcheckoutでの既存コンポーネント配置）
- `src/Sem.Web/Components/Pages/Workspace.razor`（ログインリンクと古い「後続実装」案内の削除）
- `src/Sem.Web/wwwroot/auth.js`
- `scripts/application-auth-checks.mjs`
- `deploy/APPLICATION-AUTH.md` と `deploy/APPLICATION-AUTH.ja.md`

`/Account` のセキュリティブロックと外部診断Home画面は変更しません。

## 検証

UI専用の次のチェックを実行します。

```sh
node --check src/Sem.Web/wwwroot/auth.js
node --check scripts/application-auth-checks.mjs
node scripts/application-auth-checks.mjs
```

合成チェックではSSR時の無効フォーム、明示送信、既存CSRF／options／login契約、WebAuthnアサーションのシリアライズ、タイムアウトと不正応答、未知結果時のロック／読み取り専用照合、戻り先拒否を確認します。後続のHTTPSブラウザー受入では、実際に登録済みのパスキーを使い、workspaceへの戻り、診断画面の分離、`/Account` 404維持を確認してください。.NETビルド／ホストはこのUI専用検証範囲に含みません。
