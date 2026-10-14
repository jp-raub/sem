# PKCS間接依存のセキュリティ更新

2026-10-14の新規NuGet監査で、間接依存 `System.Security.Cryptography.Pkcs` 10.0.0の警告を確認。Microsoftの [GHSA-2fmv-rv2c-59x7](https://github.com/advisories/GHSA-2fmv-rv2c-59x7) と [GHSA-846f-f764-76mx](https://github.com/advisories/GHSA-846f-f764-76mx) は10.0.0〜10.0.12を対象、修正版を10.0.13としています。前者はWindowsのEnvelopedCms.Decodeによる二次的CPU消費、後者はSignedCms.CheckSignatureでWindows／Linux／macOSが対象です。

10.0.13への直接PackageReferenceを追加し、脆弱な間接版を置き換えます。監査抑制・警告緩和・SMTP動作／認証フロー変更・DB移行・秘密情報・配布／本番設定変更はありません。今回MailKit／EF／アプリのフレームワーク版は更新しません。

検証は強制依存復元、現ソースの `dotnet list src/Sem.Web package --vulnerable --include-transitive`、Release警告エラー化、全37.NET回帰です。監査で検出なしは参照したフィードの既知問題の時点確認にすぎず、無脆弱性の保証ではありません。旧依存のCI成功を安全保証にせず、パッケージ存在だけでアプリ経路の悪用可能性や本番侵害を断定しません。

Docker／ベースランタイムの更新と再配布は別途のレビュー／承認工程です。このパッケージ修正だけでランタイム／イメージ全体の対策完了を保証せず、古い候補を配布する前に本修正を取り込む必要があります。
