---
name: sem-migration
description: SEMの保存モデル・EF Core migrationを変更するときの手順と禁止事項。モデルやmigrationを触る前に手動で呼び出す。
disable-model-invocation: true
---

# 保存モデル・migrationの変更

詳細：`docs/storage/FOUNDATION.ja.md`。

## 禁止

- 適用済みmigrationの内容・ID・snapshotの書換え。
- `model-provenance.json`のhashだけを更新して由来を偽ること（意図的なモデル変更は追加migrationと差分記録を用意する）。
- 起動すれば無条件に実DBへmigrationする変更、実DB・鍵・volumeの読み書き。
- Identityのユーザー ID・SecurityStamp・passkey・失効情報の再生成。
- `*.Designer.cs`・`ApplicationDbContextModelSnapshot.cs`の読込み・手編集（各約160KB。生成物）。

## 手順

1. 変更が要件・DB設計（Notion正本）に基づくか確認する。旧物理モデルを最新要件へ完全対応済みと扱わない。
2. モデルを変更し、追加migrationを生成する。既存migrationは触らない。
3. `model-provenance.json`と差分の記録を更新（由来が変わる理由を説明する）。
4. `python3 scripts/storage-source-checks.py`、`dotnet run --project tests/Sem.Storage.Checks -c Release`（新規初期化・再初期化・既知baseline・隔離コピー・未知DBの拒否）を実行。`/sem-verify`の保存基盤の一覧も確認する。
5. データ変更を伴う場合は、隔離した合成DBのコピーで更新前後のschema・全行の指紋を比較する。
