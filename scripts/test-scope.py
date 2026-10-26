#!/usr/bin/env python3
"""mock UIだけの変更では未接続DBの回帰を反復しない。判定不能は全検査。"""
import json
import os
import pathlib
import re
import subprocess


def storage_required(paths):
    if not paths:
        return True
    safe_files = {'README.md', 'scripts/mock-checks.py', 'scripts/runtime-checks.py',
                  'scripts/runtime-browser.cjs', 'scripts/ui-inventory-checks.py',
                  'scripts/ui-inventory-browser.cjs', 'tests/fixtures/ui-route-baseline.json'}
    safe_prefixes = ('docs/', 'src/Sem.Web/wwwroot/')
    return any(path not in safe_files and not path.startswith(safe_prefixes) for path in paths)


def changed_paths(event_name, event, diff):
    if event_name == 'pull_request':
        base = event.get('pull_request', {}).get('base', {}).get('sha')
    elif event_name == 'push':
        base = event.get('before')
    else:
        return None
    if not isinstance(base, str) or not re.fullmatch(r'[0-9a-f]{40}', base) or base == '0'*40:
        return None
    try:
        return diff(base)
    except (subprocess.SubprocessError, OSError):
        return None


def git_diff(base):
    result = subprocess.run(['git', 'diff', '--name-only', '-z', base, 'HEAD'],
                            check=True, capture_output=True)
    return result.stdout.decode('utf-8').rstrip('\0').split('\0')


def main():
    try:
        event = json.loads(pathlib.Path(os.environ['GITHUB_EVENT_PATH']).read_text())
        paths = changed_paths(os.environ.get('GITHUB_EVENT_NAME'), event, git_diff)
        required = storage_required(paths)
    except (KeyError, ValueError, TypeError, AttributeError, OSError):
        required = True
    value = 'true' if required else 'false'
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as handle:
            handle.write('storage=' + value + '\n')
    print('保存基盤の集中回帰: ' + ('実行（関連変更・判定不能・手動実行）' if required else '対象外（mock UI／資料だけの変更）'))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
