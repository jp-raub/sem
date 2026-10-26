#!/usr/bin/env python3
"""Read-only public runtime observation AFTER publication has completed.

No deploy credential/SSH/Docker. A match proves responding builds, not every replica,
image digest, business acceptance, or whether a local updater has been paused.
"""
import json
import os
from pathlib import Path
import re
import secrets
import time
import urllib.error
import urllib.request

from updater import CDException, GitHub, NoRedirect, candidate

ORIGIN = 'https://sem-test.raub.jp'
KEYS = {'schema', 'application', 'channel', 'commit', 'buildId', 'publicationRun', 'publicationAttempt', 'probe'}

class ObservationError(Exception):
    """Fixed diagnostics only, never remote response bodies or headers."""


def expected_identity(c):
    candidate(c)
    return dict(schema=1, application='SEM', channel='published', commit=c['commit'],
        buildId=f'run-{c["publication_run"]}-{c["publication_attempt"]}',
        publicationRun=str(c['publication_run']), publicationAttempt=str(c['publication_attempt']))


def fresh_json(path, probe):
    # Fixed trusted origin; caller cannot pass a host, URL, credential or redirect target.
    if path not in ('/api/version', '/health/ready') or not re.fullmatch('[0-9a-f]{32}', probe):
        raise ObservationError('INVALID_PROBE_REQUEST')
    url = ORIGIN + path + '?probe=' + probe
    request = urllib.request.Request(url, headers={'Accept': 'application/json',
        'Cache-Control': 'no-cache, no-store', 'User-Agent': 'sem-runtime-observer'})
    try:
        with urllib.request.build_opener(NoRedirect).open(request, timeout=10) as response:
            if response.status != 200 or response.geturl() != url:
                raise ObservationError('HTTP_STATUS_OR_URL')
            if response.headers.get_content_type() != 'application/json':
                raise ObservationError('NOT_JSON')
            controls = {part.strip().lower() for part in response.headers.get('Cache-Control', '').split(',')}
            if 'no-store' not in controls:
                raise ObservationError('CACHE_POLICY')
            body = response.read(8193)
            if len(body) > 8192:
                raise ObservationError('RESPONSE_LIMIT')
            # Duplicate keys are ambiguous and must not silently become a valid identity.
            def pairs(items):
                result = {}
                for key, value in items:
                    if key in result:
                        raise ObservationError('DUPLICATE_JSON_KEY')
                    result[key] = value
                return result
            return json.loads(body, object_pairs_hook=pairs)
    except (urllib.error.URLError, CDException, ValueError, UnicodeError, OSError):
        raise ObservationError('HTTPS_OR_INVALID_RESPONSE') from None


def one_sample(c, fetch=fresh_json, nonce=secrets.token_hex):
    expected = expected_identity(c)
    probe = nonce(16)
    version = fetch('/api/version', probe)
    if type(version) is not dict or set(version) != KEYS or type(version.get('schema')) is not int:
        raise ObservationError('VERSION_SCHEMA')
    if version.get('probe') != probe:
        raise ObservationError('STALE_VERSION_RESPONSE')
    if any(type(version[k]) is not type(v) or version[k] != v for k, v in expected.items()):
        raise ObservationError('RUNTIME_BUILD_MISMATCH')
    probe = nonce(16) # Independent freshness evidence for readiness, too.
    ready = fetch('/health/ready', probe)
    if type(ready) is not dict or set(ready) != {'status', 'probe'} or ready != dict(status='mock-ui-ready', probe=probe):
        raise ObservationError('NOT_FRESH_READY')


def observe(c, fetch=fresh_json, clock=time.monotonic, wait=time.sleep, budget=720, interval=20, nonce=secrets.token_hex):
    # Three consecutive fresh pairs, not just three matches with failures in between.
    expected_identity(c)
    deadline = clock() + budget
    consecutive = 0
    last = 'NO_RESPONSE'
    while clock() < deadline:
        try:
            one_sample(c, fetch, nonce)
            consecutive += 1
            last = 'FRESH_MATCH'
        except ObservationError as error:
            last = str(error)
            consecutive = 0
        if consecutive == 3 and clock() < deadline:
            return {'state': 'runtime-match', 'samples': 3, 'reason': 'FRESH_BUILD_AND_READY'}
        remaining = deadline - clock()
        if remaining > 0:
            wait(min(interval, remaining))
    return {'state': 'unconfirmed', 'samples': consecutive, 'reason': last}


def eligible(github, event):
    c = github.latest() # Same formal eligibility / current-main / release / run checks as updater.
    if c is None:
        raise ObservationError('NO_ELIGIBLE_CURRENT_CANDIDATE')
    if event is not None:
        run = event.get('workflow_run', {})
        if (run.get('repository', {}).get('full_name') != 'jp-raub/sem'
            or run.get('head_repository', {}).get('full_name') != 'jp-raub/sem'
            or run.get('event') != 'workflow_run' or run.get('head_branch') != 'main'
            or run.get('status') != 'completed' or run.get('conclusion') != 'success'
            or run.get('id') != c['publication_run'] or run.get('run_attempt') != c['publication_attempt']
            or run.get('head_sha') != c['commit']):
            raise ObservationError('TRIGGER_NOT_CURRENT_CANDIDATE')
    return c


def main():
    report = dict(state='unconfirmed', samples=0, reason='NOT_STARTED')
    try:
        if os.environ.get('GITHUB_REPOSITORY') != 'jp-raub/sem':
            raise ObservationError('WRONG_REPOSITORY')
        event_name = os.environ.get('GITHUB_EVENT_NAME')
        if event_name not in {'workflow_run', 'workflow_dispatch'}:
            raise ObservationError('WRONG_EVENT')
        event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text()) if event_name == 'workflow_run' else None
        gh = GitHub(os.environ.get('GH_TOKEN'))
        c = eligible(gh, event)
        report.update(expected=expected_identity(c), image=c['image'])
        print('Image publication verified; waiting for public runtime (up to 12 minutes).')
        report.update(observe(c))
        if report['state'] == 'runtime-match' and eligible(gh, event) != c:
            raise ObservationError('CANDIDATE_CHANGED_DURING_OBSERVATION')
    except ObservationError as error:
        report.update(state='unconfirmed', reason=str(error))
    except (CDException, KeyError, TypeError, ValueError, OSError):
        report.update(state='unconfirmed', reason='CANDIDATE_VERIFICATION_UNAVAILABLE')
    Path('verification').mkdir(exist_ok=True)
    Path('verification/runtime-observation.json').write_text(json.dumps(report, indent=2, sort_keys=True) + '\n')
    matched = report['state'] == 'runtime-match'
    text = ('公開URLで稼働ビルドとreadinessが3回連続で一致しました。' if matched else
        '公開URLでの反映は未確認です。一時停止・更新待ち・通信経路等を含み、配備破損とは断定しません。')
    summary = '## SEM 公開URLの稼働確認\n\n' + text + '\n\n状態: `' + report['state'] + '` / 分類: `' + report['reason'] + '`\n\n'
    if 'expected' in report:
        summary += '対象コミット: `' + report['expected']['commit'] + '`\n\nビルドID: `' + report['expected']['buildId'] + '`\n\n'
    summary += '対象: https://sem-test.raub.jp/about 。全レプリカ・業務受入・image digestの外部証明ではありません。\n'
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as handle:
            handle.write(summary)
    print(text + ' [' + report['reason'] + ']')
    return 0 if matched else 1

if __name__ == '__main__':
    raise SystemExit(main())
