# 公開後照合の接続先をコードから分離する

## 設定方針

公開先Originは変更可能な運用設定であり、`observe.py` の定数・テストの期待値・ログのリンクへ埋め込まない。環境変数 `SEM_PUBLIC_ORIGIN` を必須にし、GitHub Actionsでは同名の **Repository secret** から渡す。URLをソースや通常の設定変数として公開したくない運用に合わせ、値をコードへ保存しない。

- 値はHTTPSのOrigin（scheme＋host＋任意port）。例は架空の `https://runtime.example.invalid`。末尾の `/` は正規化する。
- ユーザー情報・認証情報、パス、query、fragment、空白・制御文字、不正portは拒否する。`/api/version` と `/health/ready` だけを固定パスとして付加し、リダイレクトを追わない。
- 値がなければ `PUBLIC_ORIGIN_REQUIRED`、不正なら `INVALID_PUBLIC_ORIGIN` で失敗する。既定の実ホストへ戻す、別ホストへ自動送信する、成功扱いでskipする動作はしない。
- 実値はstdout・エラー・step summary・JSON成果物へ出さない。人向けsummaryには `/about` というパスだけを記載する。完全SHA／build ID・固定診断分類等の非秘密の証跡は残す。
- Originは開始時に確定し、応答や任意パスから接続先を変更しない。HTTPS・厳密JSON・no-store・nonce・版＋準備状態の3回連続一致、正式候補・current main照合は維持する。

## 一回だけ必要な設定

リポジトリの Settings → Secrets and variables → Actions → New repository secret で、名前 `SEM_PUBLIC_ORIGIN` に実際のHTTPS Originを登録する。値はチャット・Issue・PR・共有ログへ貼らない。GitHub Actionsは公開後照合のtrusted main実行だけへ渡し、PRの集中テストには渡さない。

設定の存在はこの開発環境から未確認。**設定確認前にはこの変更をmainへ統合しない。** 未設定のまま統合すると公開後照合が設定不足で失敗する。イメージ公開と公開URL照合は別workflowのままであり、公開ジョブへ接続先を渡す必要はない。

ローカルで実環境へ照合する場合も環境変数を外部から注入する。実値の入った `.env` 等はGit管理外とし、`set -x` や環境全体の出力をしない。集中テストは架空OriginとモックHTTPを使い、実URL・実ネットワークは不要。

## 検証範囲

`python3 scripts/cd/observe_checks.py` に、設定必須・正規化・不正入力・ネットワーク呼出し前の拒否・ログ／summary／成果物への値非混入を追加した。既存の候補・連続照合・キャッシュ・nonce・JSON・リダイレクト・通信失敗の検査も維持する。

URLをコードから除くことは、公開サイト自体・DNS・通信先・過去のGit履歴を非公開にすることではない。既に公開された履歴・古いPR差分・過去ログからの削除やGit履歴の書換えは、この変更では実施しない。ソース保全と履歴追跡を維持しつつ、以後のコード・出力へ実値を新たに埋め込まない。
