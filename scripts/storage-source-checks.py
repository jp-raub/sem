#!/usr/bin/env python3
"""保全モデルの由来と、公開ホストへ未接続という段階境界を検証する。"""
import hashlib
import json
import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[1]
storage = root / 'src/Sem.Storage'
v = json.loads((storage / 'model-provenance.json').read_text())
assert v['commit'] == '812b2784212e9015ecbbce31280e48cb0bc65e84'
expected = {'model-provenance.json', 'Sem.Storage.csproj'}
models = []
for entry in v['entries']:
    path = entry['output']
    assert pathlib.PurePosixPath(path).parts[0] == 'Models'
    assert hashlib.sha256((storage / path).read_bytes()).hexdigest() == entry['outputSha256'], path
    expected.add(path)
    models += entry['models']
assert len(models) == len(set(models)) == 77
for entry in v['unchangedFiles']:
    assert entry['path'] == entry['source']
    assert hashlib.sha256((storage / entry['path']).read_bytes()).hexdigest() == entry['sha256'], entry['path']
    expected.add(entry['path'])
assert len(v['unchangedFiles']) == 91
actual = {str(p.relative_to(storage)) for p in storage.rglob('*') if p.is_file() and not {'bin', 'obj'}.intersection(p.relative_to(storage).parts)}
assert expected == actual, (expected - actual, actual - expected)
ids = sorted(re.findall(r'\[Migration\("([^"]+)"\)\]', '\n'.join(p.read_text() for p in (storage / 'Data/Migrations').glob('*.cs'))))
assert ids == v['migrationIds'] and len(ids) == 43
assert ids[0] == '20261012082319_ExternalTestBaseline'
# このPRは未接続の保存基盤。実ホストへの接続は認証保護を整えた別変更で明示的に行う。
assert 'Sem.Storage' not in (root / 'src/Sem.Web/Sem.Web.csproj').read_text()
assert 'ApplicationDbContext' not in (root / 'src/Sem.Web/Program.cs').read_text()
print('保存基盤: 77モデル・43有効migration・元の91ファイルを保持。公開ホストはDB未接続。')
