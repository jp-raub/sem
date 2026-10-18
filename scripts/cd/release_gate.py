#!/usr/bin/env python3
"""Validate the server-owned upstream run; never consume upstream artifacts/code."""
import json
import os
import re
import urllib.request

REPOSITORY = 'jp-raub/sem'
WORKFLOW = '.github/workflows/main-integration.yml'
REQUIRED = {'integration-checks', 'acceptance-browser'}


def validate(run, jobs, workflow, main_sha):
    if (run.get('repository', {}).get('full_name') != REPOSITORY
        or run.get('head_repository', {}).get('full_name') != REPOSITORY
        or run.get('event') != 'push' or run.get('head_branch') != 'main'
        or run.get('status') != 'completed' or run.get('conclusion') != 'success'
        or workflow.get('path') != WORKFLOW
        or run.get('workflow_id') != workflow.get('id')):
        raise ValueError('Not an eligible main integration run')
    sha = run.get('head_sha', '')
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise ValueError('Invalid commit')
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


def main():
    if os.environ.get('GITHUB_REPOSITORY') != REPOSITORY:
        raise ValueError('Repository not allowed')
    with open(os.environ['GITHUB_EVENT_PATH'], encoding='utf-8') as handle:
        event = json.load(handle)
    supplied = event['workflow_run']
    run_id = supplied['id']
    if type(run_id) is not int or run_id < 1:
        raise ValueError('Invalid run id')
    base = '/repos/' + REPOSITORY
    run = api(f'{base}/actions/runs/{run_id}')
    if run.get('head_sha') != supplied.get('head_sha') or run.get('run_attempt') != supplied.get('run_attempt'):
        raise ValueError('Run identity changed')
    workflow = api(f'{base}/actions/workflows/main-integration.yml')
    # Use the event's exact attempt, not the latest attempt of a concurrent rerun.
    jobs = api(f'{base}/actions/runs/{run_id}/attempts/{run["run_attempt"]}/jobs?per_page=100')
    if jobs['total_count'] != len(jobs['jobs']):
        raise ValueError('Truncated job list')
    main_sha = api(f'{base}/git/ref/heads/main')['object']['sha']
    eligible = validate(run, jobs['jobs'], workflow, main_sha)
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as handle:
        handle.write(f'eligible={str(eligible).lower()}\nsha={run["head_sha"]}\n')
    print('Eligible current main' if eligible else 'Superseded main; no image publication')


if __name__ == '__main__':
    main()
