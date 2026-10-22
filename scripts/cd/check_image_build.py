#!/usr/bin/env python3
"""Check the exact image's product assembly, not its OCI labels. No data/network."""
import json
import subprocess
import sys

if __name__ == '__main__':
    image, sha, run, attempt = sys.argv[1:]
    result = subprocess.run(['docker', 'run', '--rm', '--network', 'none', '--read-only',
        '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', image, '--build-info'],
        check=True, capture_output=True, timeout=60)
    data = json.loads(result.stdout)
    expected = dict(schema=1, application='SEM', channel='published', commit=sha,
        buildId=f'run-{run}-{attempt}', publicationRun=run, publicationAttempt=attempt, probe=None)
    if data != expected:
        raise SystemExit('Image assembly build identity mismatch')
    print('Exact image assembly commit and publication build identity matched.')
