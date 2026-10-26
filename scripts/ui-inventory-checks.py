#!/usr/bin/env python3
"""固定モックの既知ルートが台帳から脱落しないことを検証する。"""
import json
import pathlib
import re

root = pathlib.Path(__file__).resolve().parents[1]
ui = root / 'src/Sem.Web/wwwroot'
fixture = json.loads((root / 'tests/fixtures/ui-route-baseline.json').read_text())
assert fixture['schemaVersion'] == 1
assert fixture['mockCommit'] == '9cf8b031258fa1c9afe9668f54e61f002c4bce17'
cases = fixture['cases']
assert len(cases) == len({(r['actor'], r['route']) for r in cases}) == 72
actors = {'guest', 'u1', 'u2', 'u3'}
assert {r['actor'] for r in cases} == actors
routes = {r['route'] for r in cases}
for route in routes:
    assert {r['actor'] for r in cases if r['route'] == route} == actors
for row in cases:
    assert row['headings'] or row.get('deniedText'), row
    assert isinstance(row['tabs'], list)
known = {route.split('?')[0] for route in routes}
# 動的IDとクエリ文字列は上表の代表経路で検査する。新しい固定ルートは許可リストに黙って追加しない。
prefixes = {'#/account/', '#/events/', '#/profiles/'}
for source in ui.glob('*.js'):
    for route in re.findall(r'''["'](\#/[^"'`\s<>]*)["']''', source.read_text()):
        if '${' in route or route in prefixes:
            continue
        assert route.split('?')[0] in known, (source.name, route)
html = (ui / 'index.html').read_text()
scripts = re.findall(r'<script src="([^"]+)"', html)
assert scripts[-3:] == ['management-navigation.js', 'invitation-approval.js', 'invitation-administration.js']
print(f'画面台帳: {len(routes)}経路 × {len(actors)}ロール、固定ルートの脱落なし')
