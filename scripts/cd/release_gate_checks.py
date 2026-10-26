#!/usr/bin/env python3
"""Offline contract checks. No network, Docker, registry or deploy credentials."""
import copy
import unittest
import io
import json
import os
import tempfile
import urllib.error
from pathlib import Path
from unittest.mock import patch
from contextlib import redirect_stdout
from release_gate import validate, verify_upstream, GateError, cli, REPOSITORY, WORKFLOW, REQUIRED


class GateChecks(unittest.TestCase):
    def setUp(self):
        self.sha = 'a' * 40
        self.run = dict(repository={'full_name': REPOSITORY}, head_repository={'full_name': REPOSITORY},
                        event='push', head_branch='main', status='completed', conclusion='success',
                        workflow_id=17, head_sha=self.sha)
        self.jobs = [dict(name=n, status='completed', conclusion='success') for n in REQUIRED]
        self.workflow = dict(id=17, path=WORKFLOW)

    def test_current_main(self):
        self.assertTrue(validate(self.run, self.jobs, self.workflow, self.sha))

    def test_superseded(self):
        self.assertFalse(validate(self.run, self.jobs, self.workflow, 'b' * 40))

    def test_ineligible_runs(self):
        for field, value in [('event', 'pull_request'), ('event', 'workflow_dispatch'),
                             ('head_branch', 'feature'), ('status', 'in_progress'),
                             ('conclusion', 'failure'), ('conclusion', 'cancelled'),
                             ('workflow_id', 18), ('head_sha', 'x\nsha=evil')]:
            with self.subTest(field=field, value=value):
                run = copy.deepcopy(self.run)
                run[field] = value
                with self.assertRaises(ValueError):
                    validate(run, self.jobs, self.workflow, self.sha)

    def test_fork_or_wrong_repository(self):
        for field in ['repository', 'head_repository']:
            run = copy.deepcopy(self.run)
            run[field] = {'full_name': 'attacker/sem'}
            with self.assertRaises(ValueError):
                validate(run, self.jobs, self.workflow, self.sha)

    def test_wrong_workflow_path(self):
        with self.assertRaises(ValueError):
            validate(self.run, self.jobs, dict(id=17, path='.github/workflows/other.yml'), self.sha)

    def test_each_required_job(self):
        for name in REQUIRED:
            for status in ['failure', 'skipped', 'cancelled', None]:
                jobs = copy.deepcopy(self.jobs)
                next(j for j in jobs if j['name'] == name)['conclusion'] = status
                with self.subTest(name=name, status=status), self.assertRaises(ValueError):
                    validate(self.run, jobs, self.workflow, self.sha)

    def test_missing_and_duplicate_jobs(self):
        for jobs in [self.jobs[:1], self.jobs + [copy.deepcopy(self.jobs[0])]]:
            with self.assertRaises(ValueError):
                validate(self.run, jobs, self.workflow, self.sha)


class ConsistencyChecks(unittest.TestCase):
    def setUp(self):
        self.sha = 'a' * 40
        self.run = dict(id=11, run_attempt=2, repository={'full_name': REPOSITORY},
                        head_repository={'full_name': REPOSITORY}, event='push', head_branch='main',
                        status='completed', conclusion='success', workflow_id=17, head_sha=self.sha)
        self.jobs = [dict(name=n, status='completed', conclusion='success', run_id=11, run_attempt=2) for n in sorted(REQUIRED)]
        self.workflow = dict(id=17, path=WORKFLOW)
        self.waits = []
        self.snapshots = []
        self.reads = 0
        self.index = 0

    def snapshot(self, **changes):
        return dict(run=copy.deepcopy(self.run), jobs=copy.deepcopy(self.jobs), main=self.sha, **changes)

    def reader(self, path):
        self.reads += 1
        s = self.snapshots[min(self.index, len(self.snapshots)-1)]
        if path.endswith('/actions/runs/11'): return copy.deepcopy(s['run'])
        if path.endswith('/actions/workflows/main-integration.yml'): return copy.deepcopy(self.workflow)
        if path.endswith('/actions/runs/11/attempts/2/jobs?per_page=100'):
            return dict(total_count=s.get('total_count', len(s['jobs'])), jobs=copy.deepcopy(s['jobs']))
        if path.endswith('/git/ref/heads/main'):
            self.index += 1
            return {'object': {'sha': s['main']}}
        raise AssertionError('Unexpected API request')

    def verify(self):
        return verify_upstream(copy.deepcopy(self.run), self.reader, self.waits.append)

    def test_success_without_wait(self):
        self.snapshots = [self.snapshot()]
        self.assertEqual(self.verify(), (self.run, True))
        self.assertEqual(self.waits, [])

    def test_completed_run_with_pending_job_waits_then_validates(self):
        first = self.snapshot()
        first['jobs'][0].update(status='in_progress', conclusion=None)
        self.snapshots = [first, self.snapshot()]
        self.assertTrue(self.verify()[1])
        self.assertEqual(self.waits, [5])
        self.assertEqual(self.reads, 8)

    def test_pending_run_waits_same_identity(self):
        first = self.snapshot()
        first['run'].update(status='in_progress', conclusion=None)
        self.snapshots = [first, self.snapshot()]
        self.assertTrue(self.verify()[1])
        self.assertEqual(self.waits, [5])

    def test_missing_job_waits_but_never_skips_requirement(self):
        first = self.snapshot()
        first['jobs'] = first['jobs'][:1]
        self.snapshots = [first, self.snapshot()]
        self.assertTrue(self.verify()[1])
        self.assertEqual(self.waits, [5])

    def test_pending_timeout_is_bounded(self):
        first = self.snapshot()
        first['jobs'][0].update(status='in_progress', conclusion=None)
        self.snapshots = [first]
        with self.assertRaisesRegex(GateError, '^CD_GATE_API_PENDING_TIMEOUT$'): self.verify()
        self.assertEqual(self.waits, [5]*6)
        self.assertEqual(self.reads, 28)

    def test_completed_without_conclusion_is_not_success(self):
        first = self.snapshot()
        first['jobs'][0]['conclusion'] = None
        self.snapshots = [first]
        with self.assertRaisesRegex(GateError, 'PENDING_TIMEOUT'): self.verify()

    def test_terminal_job_failures_never_retry(self):
        for state in ['failure', 'cancelled', 'skipped', 'timed_out', 'action_required']:
            with self.subTest(state=state):
                self.setUp(); first = self.snapshot(); first['jobs'][0]['conclusion'] = state
                self.snapshots = [first, self.snapshot()]
                with self.assertRaisesRegex(GateError, 'REQUIRED_JOB_FAILED'): self.verify()
                self.assertEqual(self.waits, [])

    def test_terminal_run_failure_never_retry(self):
        self.snapshots = [self.snapshot()]; self.snapshots[0]['run']['conclusion'] = 'cancelled'
        with self.assertRaisesRegex(GateError, 'UPSTREAM_FAILED'): self.verify()
        self.assertEqual(self.waits, [])

    def test_duplicate_job_never_retry(self):
        first = self.snapshot(); first['jobs'].append(copy.deepcopy(first['jobs'][0])); self.snapshots = [first]
        with self.assertRaisesRegex(GateError, 'DUPLICATE_JOB'): self.verify()
        self.assertEqual(self.waits, [])

    def test_job_identity_never_rebound(self):
        for key, value in [('run_id', 12), ('run_attempt', 3)]:
            with self.subTest(key=key):
                self.setUp(); first=self.snapshot(); first['jobs'][0][key]=value; self.snapshots=[first]
                with self.assertRaisesRegex(GateError, 'JOB_IDENTITY'): self.verify()
                self.assertEqual(self.waits, [])

    def test_run_identity_change_during_wait_is_terminal(self):
        for key, value in [('id', 12), ('run_attempt', 3), ('head_sha', 'b'*40)]:
            with self.subTest(key=key):
                self.setUp(); first=self.snapshot(); first['jobs'][0].update(status='in_progress', conclusion=None)
                second=self.snapshot(); second['run'][key]=value; self.snapshots=[first,second]
                with self.assertRaisesRegex(GateError, 'RUN_IDENTITY'): self.verify()
                self.assertEqual(self.waits, [5])

    def test_fork_or_wrong_event_never_wait(self):
        for field, value in [('repository', {'full_name':'attacker/sem'}), ('event','pull_request'), ('workflow_id',18)]:
            with self.subTest(field=field):
                self.setUp(); first=self.snapshot(); first['run'][field]=value; self.snapshots=[first]
                with self.assertRaises(ValueError): self.verify()
                self.assertEqual(self.waits, [])

    def test_superseded_main_during_wait_is_not_eligible(self):
        first=self.snapshot(); first['jobs'][0].update(status='in_progress', conclusion=None)
        second=self.snapshot(); second['main']='b'*40; self.snapshots=[first,second]
        self.assertFalse(self.verify()[1]); self.assertEqual(self.waits,[5])

    def test_truncated_jobs_not_retried(self):
        first=self.snapshot(); first['total_count']=3; self.snapshots=[first]
        with self.assertRaisesRegex(GateError, 'TRUNCATED_JOBS'): self.verify()
        self.assertEqual(self.waits,[])

    def test_api_error_not_retried(self):
        def fail(path): raise urllib.error.URLError('private-auth-response')
        with self.assertRaises(urllib.error.URLError): verify_upstream(self.run, fail, self.waits.append)
        self.assertEqual(self.waits,[])

    def test_pending_event_is_rejected(self):
        self.run['status']='in_progress'
        with self.assertRaisesRegex(GateError, 'EVENT_IDENTITY'): verify_upstream(self.run, self.reader, self.waits.append)
        self.assertEqual(self.reads,0)

    def test_cli_failure_writes_no_eligibility_and_no_response(self):
        with tempfile.TemporaryDirectory() as root:
            event=Path(root)/'event.json'; event.write_text(json.dumps({'workflow_run':self.run}))
            output=Path(root)/'output'; capture=io.StringIO()
            with patch.dict(os.environ, GITHUB_REPOSITORY=REPOSITORY, GITHUB_EVENT_PATH=str(event), GITHUB_OUTPUT=str(output)):
                with patch('release_gate.verify_upstream', side_effect=GateError('CD_GATE_API_PENDING_TIMEOUT')), redirect_stdout(capture):
                    self.assertEqual(cli(),1)
                self.assertFalse(output.exists())
            self.assertEqual(capture.getvalue(),'::error title=CD gate::CD_GATE_API_PENDING_TIMEOUT\n')
            capture=io.StringIO()
            with patch('release_gate.main',side_effect=urllib.error.URLError('secret-token-value')), redirect_stdout(capture):
                self.assertEqual(cli(),1)
            self.assertNotIn('secret-token-value',capture.getvalue())
            self.assertIn('CD_GATE_API_ERROR',capture.getvalue())


if __name__ == '__main__':
    unittest.main()
