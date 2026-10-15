# テーマ値の基盤

この変更で追加するのは、純粋関数として独立テストできるカタログと継承リゾルバーです。アプリ上でテーマを選択可能にするものではなく、設定の永続化、データベーススキーマ変更、CSS描画、サイト全体で利用できる外観機能も含みません。将来の統合に向けた値の意味を定義するものであり、機能全体が完成・有効化されたという意味ではありません。

## API（`Sem.Web.Core`）

- `HexColor.Parse` / `TryParse` は ASCII の16進数字による厳密な `#RRGGBB` のみを受け付けます。値は大文字に正規化されます。CSS関数、短縮表記、アルファ値、URL、任意のCSSは拒否します。
- 不変な `ThemeDefinition` は ID、空白でない最大30文字の表示名、および型付きの4色 `Accent`、`Background`、`Surface`、`Text` を持ちます。IDは小文字ASCIIのケバブケースで、照合は大文字小文字を区別した序数比較です。
- `ThemeCatalog.Default` は不変な10件の組み込みカタログです。`ThemeCatalog(IEnumerable<ThemeDefinition>)` は注入されたカタログをコピーして検証し、空カタログ、null要素、重複IDを拒否します。リゾルバーには呼び出し側がカタログを明示的に渡し、変更可能なグローバルカタログはありません。
- `ThemeLayer` が持つのは nullable な `ThemeId` と nullable な `ThemeMode` のみです。テーマ参照はカタログの4色一式を選択します。個人・イベントレイヤーで色を個別上書きする機能はありません。モードは `Light`、`Dark`、`System` です。
- `ThemeResolver.Resolve(catalog, personal, eventLayer, site)` はテーマ選択とモードを独立して解決します。それぞれ personal → event → site の順に最初の非null値を採用します。選択されたテーマは4色一式を供給します。テーマ選択がすべてnullなら注入カタログの `green`（Forest）を選び、その必須既定エントリがなければ `fallback` の `ThemeReferenceException` で拒否します。静的な組み込み色で置き換えないため、将来のカタログ編集が反映されます。有効な明示選択がある場合、未使用のForestエントリは必須ではありません。モードがすべてnullならSystemにフォールバックします。Systemは値として保持し、サーバー側では解釈しません。
- 優先度の高いレイヤーに隠れている参照も含め、明示されたすべてのテーマIDを解決前に注入カタログで検証します。不正形式のIDはレイヤー生成時に拒否し、形式は正しいがカタログにないIDは、該当レイヤーとIDを示す `ThemeReferenceException` を送出します。明示参照を黙って別テーマに置き換えることはありません。

組み込みカタログでは元の4つのID（`green`、`blue`、`purple`、`amber`）を維持し、`rose`、`teal`、`slate`、`navy`、`coral`、`mono` を追加します。

## 検証

独立した値チェックのみを次のコマンドで実行します。

```sh
dotnet run --project tests/Sem.ThemeValueChecks/Sem.ThemeValueChecks.csproj
```

このチェック用プロジェクトは基盤ソース1ファイルを直接コンパイルし、Webアプリを参照・ビルドしません。カタログ値、入力検証、不変性、決定的な継承、カスタムカタログ注入、無効な明示参照を検証します。データベース、UI、描画、永続化、デプロイについての検証や完了を主張するものではありません。
