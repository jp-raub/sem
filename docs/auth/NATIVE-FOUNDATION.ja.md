# 固定UIへ接続するための標準パスキー認証部品

## 位置づけ

要件F02・D04・T04・N01と、機能接続順1の認証部分。保全版 `812b2784212e9015ecbbce31280e48cb0bc65e84` の `ApplicationLoginEndpoints` を分離し、`Sem.Storage` のIdentityモデル／旧43移行へ接続した。旧画面・巨大サービス群・通知ワーカーは戻さない。

**分離段階（PR134）では公開Webホストは未参照だった。後続の[製品接続](PRODUCT-CONNECTION.ja.md)では明示モードだけで参照する。既定の公開UIは引き続きデモで、実配置の有効化済みとはしない。** 先に標準WebAuthn署名検証・セッション・HTTP境界を合成HTTPSで検査する段階であり、隔離fixtureの登録経路を製品へ持ち込まない。

## 実装した契約

- ASP.NET Core Identityの `MakePasskeyRequestOptionsAsync`／`PasskeySignInAsync` を使用する。独自の署名検証はしない。RPは明示HTTPS Originのhost、ユーザー検証はrequired。メール指定の既存経路であり、メールなしログインは後続。
- POSTのみ・query禁止、厳密JSON／UTF-8、余分／重複項目、圧縮・本文4KiB／64KiB・credential上限、Origin一致、標準CSRF、IP別毎分60要求・待ち行列なしを扱う。Origin／鍵領域に公開ホストの既定値を置かず、秘密値を含まない分類で失敗する。
- 旧cookie名 `__Host-SEM-Auth`、DP用途分離名 `SEM.ExternalTest`、Identity schema3、session claim名、大文字SHA-256を維持。Secure／HttpOnly／root path。定期stamp更新では同じID・stampの一意なsessionだけ継承する。
- 毎要求で現在のEnabled・EmailConfirmed・SecurityStamp・失効テーブルを確認。本人セッションGETはID／表示名／現在の全体管理フラグだけを返し、メール・キー・stamp・sessionは含めない。ログアウトはトランザクション内で再確認し、現sessionの失効と同じsessionに結び付くPush端末の解除を保存してからCookieを終了する。
- 明示プロキシが0件ならForwardedHeaders.None。指定されたIPだけ、一段のX-Forwarded-For／Protoを信頼する。実効HTTPS・Host／portも検査する。
- 標準Identityのchallengeへ、有界4096件・5分・一回限りのサーバー側消費を追加。ランダムHttpOnly cookieと標準optionsのchallengeのハッシュを結び付け、署名／cookieの再送・並行消費・別challenge転用を拒否する。暗号検証は引き続き標準基盤が担当する。

## 段階境界・制約

challenge台帳はプロセス内。再起動すると未完了試行は拒否され、optionsからやり直す。署名済みログインCookie・登録キー・失効情報・DP鍵はDB／鍵領域に残る。複数プロセス間の途中試行継続は保証しない（別プロセスでは安全に拒否する）。複数replica対応時には共有された原子的消費ストアが別途必要。無制限メモリや、再起動で旧challengeを再許可する設計にしない。

不在／無効ユーザーのエラー本文は共通だが、optionsの成功有無や時間差からアカウント存在を完全に隠す保証ではない。旧キー・Cookie・実鍵ファイルの実環境受入、PostgreSQL、メール、初回招待登録、追加／復旧／メール変更、最終ログイン記録と業務UI接続は別工程。公開先の実DB・鍵・private fileは今回読み書きしない。

製品ホストへの次の接続では、既存保全／更新・DBlease・配置設定と認証middleware順を統合し、固定mockのログインDOMへ接続する。デモロール選択やlocalStorageを本人認証にせず、未接続の業務操作を成功表示しない。

## 高速回帰

```sh
dotnet run --project tests/Sem.Authentication.Checks -c Release
dotnet publish tests/Sem.Authentication.Fixture -c Release -o /tmp/sem-auth-fixture
SEM_AUTH_FIXTURE_DLL=/tmp/sem-auth-fixture/Sem.Authentication.Fixture.dll node scripts/authentication-native-browser.cjs
```

Playwrightと.NETが利用可能な環境で実行する。ブラウザーは専用のloopback HTTPSホスト・自己署名証明書・新規合成DB・CDP仮想認証器を使用する。再起動を含めCookie／credential／仮想キーはメモリだけで保持し、認証状態ファイルを作らない。合成ホスト終了後、自分で作った一時領域だけを削除する。実DB・秘密値・物理認証器の操作は不要。

44項目の部品検査に、Origin／プロキシ設定、失効、重複claim、stamp更新、大文字hash、challenge同時8要求／転用／期限／容量を含む。HTTPS52項目では標準登録・署名ログイン・厳密HTTP・再送・無効化・stamp・ログアウト前cookieの拒否・同じDBと鍵での再起動後cookie検証／同じキーでの再ログイン・明示プロキシのHTTPS終端・実429を確認する。物理端末／旧実データの受入の代用ではない。

CIは既知のUI／資料だけなら未接続認証の実行を反復しない。認証・DB・ホスト・CI・未知ファイル・差分不明は保存基盤と同じfail-closed条件で部品とHTTPSを実行する。必須job名と8分上限・固定mock／版検査・公開条件を維持する。

## 試行中の失敗を区別

初回は起動完了前の接続を修正。no-store完全一致の期待は標準cookie更新が付ける複数directiveを正しく扱うよう修正した。標準APIの開始済みchallengeなし再送が例外になることを確認し、固定エラー分類とサーバー側の一回消費を追加。これらの失敗を最終成功へ流用せず、新規合成DBから通し直した。
