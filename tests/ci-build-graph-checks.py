#!/usr/bin/env python3
"""Dependency-free regression tests for CI orchestration, not application tests."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class BuildGraphChecks(unittest.TestCase):
    def exercise(self, fail='', tests=True):
        with tempfile.TemporaryDirectory(prefix='sem build graph ') as folder:
            root = Path(folder)
            (root / 'scripts').mkdir()
            shutil.copy(ROOT / 'scripts/build-integration-projects.sh', root / 'scripts')
            projects = ['src/Sem.Web/Sem.Web.csproj']
            if tests:
                projects += ['tests/First/First.csproj', 'tests/Second/Second.csproj']
            for name in projects:
                p = root / name
                p.parent.mkdir(parents=True, exist_ok=True)
                p.write_text('<Project />')
            tools = root / 'tools'
            tools.mkdir()
            fake = tools / 'dotnet'
            fake.write_text('''#!/usr/bin/env python3
import json,os,sys
from pathlib import Path
args=sys.argv[1:]
with open(os.environ['CALLS'],'a') as log: log.write(json.dumps(args)+'\\n')
if args[0]=='new':
 p=Path(args[args.index('--output')+1]);p.mkdir(exist_ok=True)
 (p/'sem-integration.slnx').write_text('<Solution />')
if args[0]==os.environ.get('FAIL_COMMAND'):sys.exit(23)
''')
            fake.chmod(0o755)
            calls = root / 'calls.jsonl'
            env = dict(os.environ, PATH=str(tools)+os.pathsep+os.environ['PATH'],
                       CALLS=str(calls), FAIL_COMMAND=fail)
            result = subprocess.run(['bash', 'scripts/build-integration-projects.sh'],
                                    cwd=root, env=env, capture_output=True, text=True)
            commands = [json.loads(line) for line in calls.read_text().splitlines()]
            solution = Path(commands[0][commands[0].index('--output')+1])
            self.assertFalse(solution.exists(), 'temporary solution must be removed on success/failure')
            return result, commands, projects

    def test_builds_complete_graph_once_including_shared_app(self):
        result, calls, projects = self.exercise()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([x[0] for x in calls], ['new', 'sln', 'restore', 'build'])
        self.assertEqual(calls[1][3:], projects)
        self.assertIn('--no-restore', calls[-1])
        self.assertIn('--maxcpucount:2', calls[-1])
        self.assertNotIn('--no-dependencies', calls[-1])

    def test_restore_failure_cannot_reuse_stale_output(self):
        result, calls, _ = self.exercise(fail='restore')
        self.assertEqual(result.returncode, 23)
        self.assertNotIn('build', [x[0] for x in calls])

    def test_build_failure_propagates(self):
        result, _, _ = self.exercise(fail='build')
        self.assertEqual(result.returncode, 23)

    def test_historical_app_without_dedicated_tests_still_builds(self):
        result, calls, projects = self.exercise(tests=False)
        self.assertEqual(result.returncode, 0)
        self.assertEqual(calls[1][3:], projects)

    def test_execution_remains_complete_serial_and_isolated(self):
        script = (ROOT / 'scripts/integration-checks.sh').read_text()
        self.assertIn('for project in tests/*/*.csproj; do', script)
        self.assertIn('sudo -E unshare --net', script)
        self.assertIn('--no-new-privs --bounding-set=-all', script)
        self.assertIn('--no-build --no-restore', script)
        self.assertNotIn('dotnet build "$project"', script)
        self.assertIn('bash scripts/build-integration-projects.sh', script)
        self.assertIn('docker restart sem-integration', script)

if __name__ == '__main__':
    unittest.main()
