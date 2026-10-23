import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
import urllib.error

spec = importlib.util.spec_from_file_location('resume', Path(__file__).resolve().parents[1] / 'scripts/dev/resume_workspace.py')
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
SHA = 'a' * 40

def bundle(extra=None):
    files = [('repo/src/Sem.Web/Sem.Web.csproj', b'<Project/>', None)] + (extra or [])
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode='w:gz') as t:
        for name, body, kind in files:
            item = tarfile.TarInfo(name); item.size = len(body); item.mode = 0o755 if name.endswith('.sh') else 0o644
            if kind: item.type = kind; item.linkname = '/tmp/outside'; item.size = 0
            t.addfile(item, io.BytesIO(body) if not kind else None)
    return output.getvalue()

class Checks(unittest.TestCase):
    def test_full_sha_only(self):
        for value in ('main', 'a'*7, 'A'*40, 'a'*40+'\n', '../x'):
            with self.assertRaises(m.ResumeError): m.validate_commit(value)
    def test_fresh_and_idempotent(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)/'work'; data = bundle([('repo/run.sh', b'echo ok', None)])
            status, record = m.restore(SHA, root, lambda _: data)
            self.assertEqual(status, 'restored'); self.assertEqual(record['tests'], 'not-run')
            self.assertTrue(record['files']['run.sh']['executable'])
            self.assertEqual(m.restore(SHA, root, lambda _: self.fail('unexpected GET'))[0], 'verified-existing')
    def test_changed_file_preserved(self):
        self.assert_preserved('src/Sem.Web/Sem.Web.csproj', b'unsaved')
    def test_added_file_preserved(self):
        self.assert_preserved('draft.txt', b'unsaved')
    def assert_preserved(self, name, body):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'work'; m.restore(SHA, root, lambda _: bundle()); (root/name).write_bytes(body)
            with self.assertRaises(m.ResumeError): m.restore(SHA, root)
            self.assertEqual((root/name).read_bytes(),body)
    def test_missing_file_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'work';m.restore(SHA,root,lambda _:bundle());(root/'src/Sem.Web/Sem.Web.csproj').unlink()
            with self.assertRaises(m.ResumeError):m.restore(SHA,root)
    def test_different_commit_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'work';m.restore(SHA,root,lambda _:bundle())
            with self.assertRaises(m.ResumeError):m.restore('b'*40,root)
    def test_unknown_existing_directory_preserved(self):
        with tempfile.TemporaryDirectory() as d:
            with self.assertRaises(m.ResumeError):m.restore(SHA,Path(d))
    def test_checkpoint_without_source_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'work';root.with_name('work.resume.json').write_text('{}')
            with self.assertRaises(m.ResumeError):m.restore(SHA,root)
    def test_symlink_destination_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'work';root.symlink_to(Path(d)/'missing')
            with self.assertRaises(m.ResumeError):m.restore(SHA,root)
    def test_unsafe_archives(self):
        for name,kind in [('../escape',None),('/absolute',None),('repo/../escape',None),('repo/link',tarfile.SYMTYPE),('repo/hard',tarfile.LNKTYPE),('other/file',None),('repo/src/Sem.Web/Sem.Web.csproj',None)]:
            with self.subTest(name=name,kind=kind),tempfile.TemporaryDirectory() as d:
                root=Path(d)/'work'
                with self.assertRaises(m.ResumeError):m.restore(SHA,root,lambda _:bundle([(name,b'x',kind)]))
                self.assertFalse(root.exists());self.assertEqual(list(Path(d).iterdir()),[])
    def test_entry_size_limit(self):
        old=m.MAX_FILE
        try:
            m.MAX_FILE=3
            with tempfile.TemporaryDirectory() as d:
                with self.assertRaises(m.ResumeError):m.restore(SHA,Path(d)/'work',lambda _:bundle())
        finally:m.MAX_FILE=old
    def test_download_retries_read_only_transient(self):
        calls=[];delays=[]
        def get(request,timeout):
            calls.append((request.get_method(),request.full_url,timeout))
            if len(calls)<3:raise urllib.error.HTTPError(request.full_url,503,'',{},None)
            return io.BytesIO(b'ok')
        self.assertEqual(m.download(SHA,get,delays.append),b'ok');self.assertEqual(delays,[1,2]);self.assertEqual(len(calls),3)
        self.assertTrue(all(c[0]=='GET' for c in calls))
    def test_download_does_not_retry_auth_or_rate_limit(self):
        for code in (401,403,404,429):
            calls=[]
            def get(request,timeout):
                calls.append(1);raise urllib.error.HTTPError(request.full_url,code,'private',{},None)
            with self.assertRaises(m.ResumeError):m.download(SHA,get,lambda _:self.fail('retry'))
            self.assertEqual(len(calls),1)
    def test_network_retry_bounded(self):
        calls=[]
        def get(request,timeout):calls.append(1);raise TimeoutError('private')
        with self.assertRaises(m.ResumeError):m.download(SHA,get,lambda _:None)
        self.assertEqual(len(calls),3)
    def test_lock_preserved(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'work';lock=Path(d)/'work.resume.lock';lock.write_text('another writer')
            with self.assertRaises(m.ResumeError):m.restore(SHA,root,lambda _:bundle())
            self.assertEqual(lock.read_text(),'another writer');self.assertFalse(root.exists())

class RunnerChecks(unittest.TestCase):
    def setUp(self):
        spec=importlib.util.spec_from_file_location('runner',Path(__file__).resolve().parents[1]/'scripts/dev/run_in_workspace.py')
        self.runner=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.runner)
    def test_missing_workspace_never_executes(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(self.runner.run(Path(d)/'missing',['anything'],lambda *a,**k:self.fail('executed')),2)
    def test_child_failure_is_returned_without_retry(self):
        import subprocess
        calls=[]
        def invoke(cmd,**kwargs):calls.append((cmd,kwargs));return subprocess.CompletedProcess(cmd,23)
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(self.runner.run(d,['literal','a b'],invoke),23)
        self.assertEqual(len(calls),1);self.assertEqual(calls[0][0],['literal','a b']);self.assertFalse(calls[0][1]['check'])
    def test_start_error_is_not_retried(self):
        calls=[]
        def invoke(*a,**k):calls.append(1);raise OSError('private path')
        with tempfile.TemporaryDirectory() as d:self.assertEqual(self.runner.run(d,['x'],invoke),2)
        self.assertEqual(len(calls),1)
    def test_missing_command_does_not_execute(self):
        with tempfile.TemporaryDirectory() as d:self.assertEqual(self.runner.run(d,[],lambda *a,**k:self.fail('executed')),2)

if __name__=='__main__':unittest.main()
