#!/usr/bin/env python3
"""Validate the server-owned upstream run; never consume upstream artifacts/code."""
import json
import os
import re
import time
import urllib.error
import urllib.request

REPOSITORY = 'jp-raub/sem'
WORKFLOW = '.github/workflows/main-integration.yml'
REQUIRED = {'integration-checks', 'acceptance-browser'}


def validate_identity(run, workflow):
    if (run.get('repository', {}).get('full_name') != REPOSITORY
        or run.get('head_repository', {}).get('full_name') != REPOSITORY
        or run.get('event') != 'push' or run.get('head_branch') != 'main'
        or workflow.get('path') != WORKFLOW
        or run.get('workflow_id') != workflow.get('id')):
        raise ValueError('Not an eligible main integration run')
    sha = run.get('head_sha', '')
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('Invalid commit')


def validate(run, jobs, workflow, main_sha):
    validate_identity(run, workflow)
    sha = run['head_sha']
    if run.get('status') != 'completed' or run.get('conclusion') != 'success':
        raise ValueError('Not an eligible main integration run')
    for name in REQUIRED:
        matching = [j for j in jobs if j.get('name') == name]
        if len(matching) != 1 or matching[0].get('conclusion') != 'success' or matching[0].get('status') != 'completed':
            raise ValueError('Required job not successful: ' + name)
    # New main supersedes this run; do not deploy an old successful run after a new push.
    return sha == main_sha


def api(path):
    if not path.startswith('/repos/' + REPOSITORY + '/'):
        raise ValueError('API outside repository')
    request = urllib.request.Request('https://api.github.com' + path, headers={
        'Authorization': 'Bearer ' + os.environ['GH_TOKEN'],
        'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
        'User-Agent': 'sem-cd-release-gate'})
    with urllib.request.urlopen(request, timeout=30) as response:
        if response.geturl() != 'https://api.github.com' + path:
            raise ValueError('Unexpected API redirect')
        return json.load(response)


class GateError(ValueError):
    """Fixed, non-secret diagnostic classification."""


def verify_upstream(supplied, read=api, wait=time.sleep):
    run_id = supplied.get('id')
    attempt = supplied.get('run_attempt')
    if (type(run_id) is not int or run_id < 1 or type(attempt) is not int or attempt < 1
        or supplied.get('status') != 'completed' or supplied.get('conclusion') != 'success'):
        raise GateError('CD_GATE_EVENT_IDENTITY')
    base = '/repos/' + REPOSITORY
    # Seven snapshots and six 5-second waits at most. HTTP/auth errors are not retried.
    for observation in range(7):
        run = read(f'{base}/actions/runs/{run_id}')
        if (run.get('id') != run_id or run.get('head_sha') != supplied.get('head_sha')
            or run.get('run_attempt') != attempt):
            raise GateError('CD_GATE_RUN_IDENTITY')
        workflow = read(f'{base}/actions/workflows/main-integration.yml')
        validate_identity(run, workflow)
        jobs = read(f'{base}/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100')
        if jobs['total_count'] != len(jobs['jobs']):
            raise GateError('CD_GATE_TRUNCATED_JOBS')
        pending = False
        if run.get('status') == 'completed':
            if run.get('conclusion') is None:
                pending = True
            elif run.get('conclusion') != 'success':
                raise GateError('CD_GATE_UPSTREAM_FAILED')
        elif run.get('status') in {'queued', 'in_progress', 'pending', 'waiting', 'requested'} and run.get('conclusion') is None:
            pending = True
        else:
            raise GateError('CD_GATE_UPSTREAM_FAILED')
        for name in REQUIRED:
            matching = [j for j in jobs['jobs'] if j.get('name') == name]
            if len(matching) > 1:
                raise GateError('CD_GATE_DUPLICATE_JOB')
            if not matching:
                pending = True
                continue
            job = matching[0]
            if job.get('run_id') != run_id or job.get('run_attempt') != attempt:
                raise GateError('CD_GATE_JOB_IDENTITY')
            if job.get('status') == 'completed':
                if job.get('conclusion') is None:
                    pending = True
                elif job.get('conclusion') != 'success':
                    raise GateError('CD_GATE_REQUIRED_JOB_FAILED')
            elif job.get('status') in {'queued', 'in_progress', 'pending', 'waiting', 'requested'} and job.get('conclusion') is None:
                pending = True
            else:
                raise GateError('CD_GATE_REQUIRED_JOB_FAILED')
        main_sha = read(f'{base}/git/ref/heads/main')['object']['sha']
        if run['head_sha'] != main_sha:
            return run, False
        if not pending:
            return run, validate(run, jobs['jobs'], workflow, main_sha)
        if observation == 6:
            raise GateError('CD_GATE_API_PENDING_TIMEOUT')
        wait(5)
    raise AssertionError('Unreachable')


def main():
    if os.environ.get('GITHUB_REPOSITORY') != REPOSITORY:
        raise GateError('CD_GATE_REPOSITORY')
    with open(os.environ['GITHUB_EVENT_PATH'], encoding='utf-8') as handle:
        event = json.load(handle)
    run, eligible = verify_upstream(event['workflow_run'])
    # No eligibility output is written until the complete identity/jobs check passes.
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as handle:
        handle.write(f'eligible={str(eligible).lower()}\nsha={run["head_sha"]}\n')
    print('Eligible current main' if eligible else 'Superseded main; no image publication')


def cli():
    try:
        main()
    except GateError as error:
        print('::error title=CD gate::' + str(error))
        return 1
    except urllib.error.URLError:
        print('::error title=CD gate::CD_GATE_API_ERROR')
        return 1
    except (ValueError, KeyError, TypeError):
        print('::error title=CD gate::CD_GATE_INVALID_RESPONSE')
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(cli())
