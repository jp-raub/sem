#!/usr/bin/env python3
"""Offline contract checks. No network, Docker, registry or deploy credentials."""
import copy
import unittest
from release_gate import validate, REPOSITORY, WORKFLOW, REQUIRED


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


if __name__ == '__main__':
    unittest.main()
