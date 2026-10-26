#!/usr/bin/env python3
import importlib.util
import subprocess
import unittest
from pathlib import Path

spec=importlib.util.spec_from_file_location('scope',Path(__file__).with_name('test-scope.py'))
scope=importlib.util.module_from_spec(spec)
spec.loader.exec_module(scope)

class Checks(unittest.TestCase):
    def test_mock_and_docs_only(self):
        for paths in [['docs/ui/example.md'],['README.md'],['src/Sem.Web/wwwroot/app.js'],
                      ['scripts/ui-inventory-browser.cjs','tests/fixtures/ui-route-baseline.json']]:
            self.assertFalse(scope.storage_required(paths))
    def test_related_or_unknown_requires_storage(self):
        for path in ['src/Sem.Storage/Data/ApplicationDbContext.cs','src/Sem.Storage/Data/Migrations/new.cs',
                     'tests/Sem.Storage.Checks/Program.cs','tools/Sem.ModelExtractor/Program.cs',
                     'src/Sem.Web/Program.cs','src/Sem.Web/Sem.Web.csproj',
                     'src/Sem.Web/Runtime/Authentication.cs','.github/workflows/main-integration.yml',
                     'scripts/test-scope.py','global.json','unknown']:
            self.assertTrue(scope.storage_required(['docs/ui/example.md',path]))
    def test_empty_or_unknown_is_full(self):
        self.assertTrue(scope.storage_required([]));self.assertTrue(scope.storage_required(None))
    def test_pr_and_push_base(self):
        sha='a'*40
        for name,event in [('pull_request',{'pull_request':{'base':{'sha':sha}}}),('push',{'before':sha})]:
            self.assertEqual(scope.changed_paths(name,event,lambda base:['docs/test.md'] if base==sha else []),['docs/test.md'])
    def test_manual_missing_invalid_new_branch(self):
        for name,event in [('workflow_dispatch',{}),('push',{}),('push',{'before':'0'*40}),
                           ('push',{'before':'--bad'}),('pull_request',{})]:
            self.assertIsNone(scope.changed_paths(name,event,lambda base:self.fail('diffしてはいけない')))
    def test_git_failure_is_full(self):
        def fail(base):raise subprocess.CalledProcessError(1,['git'])
        self.assertIsNone(scope.changed_paths('push',{'before':'a'*40},fail))
    def test_deleted_storage_and_mixed_changes_are_full(self):
        self.assertTrue(scope.storage_required(['src/Sem.Storage/deleted.cs','src/Sem.Web/wwwroot/app.js']))

if __name__=='__main__':unittest.main()
