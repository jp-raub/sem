#!/usr/bin/env python3
"""Synthetic controller/API/backup tests. Never connect to NAS, registry or Docker."""
import copy
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

from updater import (Updater, Docker, GitHub, CDException, candidate, atomic_json, read_json,
                     locked, secure_path, IMAGE, REPOSITORY, CANDIDATE_KEYS)


def good():
    return dict(deployment_mode='auto', schema=1, repository=REPOSITORY, commit='a'*40, image=IMAGE+'@sha256:'+'b'*64,
                platform='linux/amd64', integration_run=11, integration_attempt=1,
                publication_run=22, publication_attempt=1)


class Source:
    def __init__(self):
        self.c = good()
        self.current = self.c['commit']
        self.reads = 0

    def latest(self):
        self.reads += 1
        return self.c

    def current_main(self):
        return self.current


class Engine:
    def __init__(self):
        self.calls = []
        self.fail = None
        self.healthy_result = True

    def call(self, name):
        self.calls.append(name)
        if self.fail == name:
            raise CDException('Synthetic failure')

    def container(self):
        self.call('container')
        return {'Id': 'c'*64}

    def is_current(self, c):return True

    def pull(self, c):
        self.call('pull')
        return {}

    def preflight(self, old, image, root):
        self.call('preflight')
        return {}

    def stop(self, old): self.call('stop')
    def backup(self, old, root, effective): self.call('backup')
    def restart_old(self, old): self.call('restart_old')
    def activate(self, c):
        self.call('activate')
        return {}
    def healthy(self, current):
        self.call('healthy')
        return self.healthy_result
    def stop_failed(self): self.call('stop_failed')


class ControllerChecks(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        (self.root/'backup').mkdir()
        self.source, self.engine = Source(), Engine()
        self.updater = Updater({'backup_root': str(self.root/'backup')}, self.root, self.source, self.engine)
        atomic_json(self.updater.path, {'paused': False, 'blocked': False, 'phase': 'ready'})

    def tearDown(self): self.tmp.cleanup()

    def test_initial_default_paused(self):
        self.updater.path.unlink()
        self.updater.tick()
        self.assertEqual(self.source.reads, 0)
        self.assertEqual(self.engine.calls, [])

    def test_order_and_success(self):
        self.updater.tick()
        self.assertEqual(self.engine.calls, ['container','pull','preflight','stop','backup','activate','healthy'])
        state = self.updater.state()
        self.assertEqual(state['phase'], 'ready')
        self.assertFalse(state['blocked'])
        self.assertEqual(state['current_image'], good()['image'])
        self.assertTrue(Path(state['backup']).is_dir())

    def test_no_same_digest_replay(self):
        self.updater.tick()
        calls = self.engine.calls.copy()
        self.updater.tick()
        self.assertEqual(self.engine.calls, calls)

    def test_manual_policy_never_downloads_or_stops(self):
        self.source.c['deployment_mode']='manual'
        self.updater.tick()
        self.assertTrue(self.updater.state()['paused'])
        self.assertEqual(self.updater.state()['phase'],'manual-required')
        self.assertEqual(self.engine.calls,[])

    def test_no_candidate(self):
        self.source.c = None
        self.updater.tick()
        self.assertEqual(self.engine.calls, [])

    def test_pause_and_block_no_network_or_docker(self):
        for field in ['paused', 'blocked']:
            state = dict(paused=False, blocked=False, phase='failed-after-start')
            state[field] = True
            atomic_json(self.updater.path, state)
            self.updater.tick()
        self.assertEqual(self.source.reads, 0)
        self.assertEqual(self.engine.calls, [])

    def test_killed_process_phase_never_replays(self):
        for phase in ['stopping','backup','activating','health']:
            atomic_json(self.updater.path, dict(paused=False, blocked=False, phase=phase))
            self.updater.tick()
            self.assertTrue(self.updater.state()['blocked'])
        self.assertEqual(self.engine.calls, [])

    def test_superseded_after_download_not_stopped(self):
        self.source.current = 'f'*40
        self.updater.tick()
        self.assertNotIn('stop', self.engine.calls)
        self.assertEqual(self.updater.state()['phase'], 'superseded')

    def test_preflight_failure_never_stops(self):
        for fail in ['container','pull','preflight']:
            self.engine.calls=[]
            self.engine.fail=fail
            self.updater.tick()
            self.assertNotIn('stop', self.engine.calls)
            self.assertFalse(self.updater.state()['blocked'])

    def test_backup_failure_restarts_only_old_container_and_blocks(self):
        self.engine.fail='backup'
        self.updater.tick()
        self.assertIn('restart_old', self.engine.calls)
        self.assertNotIn('activate', self.engine.calls)
        self.assertTrue(self.updater.state()['blocked'])
        calls=self.engine.calls.copy()
        self.updater.tick()
        self.assertEqual(self.engine.calls,calls)

    def test_after_start_failure_no_old_image_restart(self):
        for failure in ['activate', 'health']:
            with self.subTest(failure=failure):
                self.setUp_new_state()
                if failure=='activate': self.engine.fail='activate'
                else: self.engine.healthy_result=False
                self.updater.tick()
                self.assertIn('stop_failed',self.engine.calls)
                self.assertNotIn('restart_old',self.engine.calls)
                self.assertEqual(self.updater.state()['phase'],'failed-after-start')
                self.assertTrue(self.updater.state()['blocked'])
                calls=self.engine.calls.copy()
                self.updater.tick()
                self.assertEqual(self.engine.calls,calls)

    def setUp_new_state(self):
        for p in (self.root/'backup').iterdir(): p.rmdir()
        atomic_json(self.updater.path, {'paused':False,'blocked':False,'phase':'ready'})
        self.engine.calls=[]
        self.engine.fail=None
        self.engine.healthy_result=True

    def test_intent_durable_before_stop(self):
        original=self.engine.stop
        def stop(old):
            state=self.updater.state()
            self.assertTrue(state['blocked'])
            self.assertEqual(state['phase'],'stopping')
            original(old)
        self.engine.stop=stop
        self.updater.tick()

    def test_lock_prevents_concurrent_control(self):
        with locked(self.root):
            with self.assertRaises(CDException):
                with locked(self.root): pass

    def test_invalid_state_not_silently_reset(self):
        self.updater.path.write_text('not json')
        with self.assertRaises(CDException): self.updater.tick()

    def test_candidate_strict(self):
        self.assertEqual(candidate(good()),good())
        for key,value in [('schema',True),('commit','main'),('image','sem:latest'),('image',good()['image']+'\ncommand'),
                          ('platform','linux/arm64'),('repository','other/repo'),('publication_run',True),('integration_attempt',0)]:
            c=good();c[key]=value
            with self.subTest(key=key,value=value),self.assertRaises(CDException): candidate(c)
        for c in [{**good(),'command':'docker rm'}, {k:v for k,v in good().items() if k!='image'}]:
            with self.assertRaises(CDException):candidate(c)

    def test_digest_image_metadata(self):
        c=good()
        image=dict(Os='linux', Architecture='amd64',Config={'User':'app','Labels':{'org.opencontainers.image.revision':c['commit'],
                   'org.opencontainers.image.source':'https://github.com/'+REPOSITORY}},RepoDigests=[c['image']])
        Docker.check_image(image,c)
        for field,value in [('Architecture','arm64'),('RepoDigests',[])]:
            bad=copy.deepcopy(image);bad[field]=value
            with self.assertRaises(CDException):Docker.check_image(bad,c)
        for user in ['root','0','']:
            bad=copy.deepcopy(image);bad['Config']['User']=user
            with self.assertRaises(CDException):Docker.check_image(bad,c)

    def test_mount_binding(self):
        d=Docker({'project':'sem-external-test','service':'sem','data_type':'volume','data_source':'existing'}, self.root)
        current={'Config':{'Labels':{'com.docker.compose.project':'sem-external-test','com.docker.compose.service':'sem'}},
                 'Mounts':[{'Destination':'/data','Type':'volume','Name':'existing','RW':True}]}
        d.check_container(current)
        for field,value in [('Name','empty-new-volume'),('RW',False)]:
            bad=copy.deepcopy(current);bad['Mounts'][0][field]=value
            with self.assertRaises(CDException):d.check_container(bad)

    def test_private_output_not_written_on_error(self):
        self.engine.preflight=lambda *a: (_ for _ in ()).throw(RuntimeError('SMTP_PASSWORD=private'))
        self.updater.tick()
        self.assertNotIn('SMTP_PASSWORD',self.updater.path.read_text())


class PreflightChecks(unittest.TestCase):
    def test_capacity_and_proposed_mount_checked_before_stop(self):
        with tempfile.TemporaryDirectory() as root:
            root=Path(root)
            engine=Docker(dict(service='sem',data_type='volume',data_source='existing'),root)
            effective={'services':{'sem':{'volumes':[dict(target='/data',type='volume',source='data')]}},
                       'volumes':{'data':{'name':'existing'}}}
            engine.compose=lambda *a:json.dumps(effective).encode()
            old={'State':{'Running':True},'Id':'c','Image':'old'}
            def execute(args,output=None,timeout=900):
                return b'1 /data' if args[1]=='exec' else json.dumps([dict(Size=100)]).encode()
            with patch('updater.run',execute):
                with patch('updater.shutil.disk_usage',return_value=type('Usage',(),{'free':0})()):
                    with self.assertRaises(CDException):engine.preflight(old,{},root)
                with patch('updater.shutil.disk_usage',return_value=type('Usage',(),{'free':10**12})()):
                    engine.preflight(old,{},root)
                    effective['volumes']['data']['name']='different-empty-volume'
                    with self.assertRaises(CDException):engine.preflight(old,{},root)
                    effective['volumes']['data']['name']='existing'
                    effective['services']['sem']['volumes'][0]['read_only']=True
                    with self.assertRaises(CDException):engine.preflight(old,{},root)


class BackupChecks(unittest.TestCase):
    def test_backup_stream_ownership_keys_wal_and_manifest(self):
        with tempfile.TemporaryDirectory() as root:
            root=Path(root)
            source=root/'compose.json';source.write_text('{}')
            dest=root/'backup';dest.mkdir()
            cfg={'compose_files':[str(source)],'env_file':None}
            data=io.BytesIO()
            with tarfile.open(fileobj=data,mode='w') as archive:
                for name,body in [('sem.db',b'synthetic-db'),('sem.db-wal',b'wal'),('keys/key.xml',b'fake-key'),('images/avatar.png',b'png')]:
                    entry=tarfile.TarInfo(name);entry.uid=1654;entry.gid=1654;entry.mode=0o600;entry.size=len(body)
                    archive.addfile(entry,io.BytesIO(body))
            def execute(args,output=None,timeout=900):
                Path(output).write_bytes(data.getvalue() if args[1]=='cp' else b'synthetic-image')
            with patch('updater.run',execute):
                Docker(cfg,root).backup({'Id':'c'*64,'Image':'sha256:'+'d'*64},dest,{'services':{'sem':{'environment':{'SMTP_PASSWORD':'synthetic-only'}}}})
            manifest=read_json(dest/'SHA256SUMS.json',{})
            self.assertIn('data.tar',manifest)
            self.assertIn('old-image.tar',manifest)
            self.assertEqual(stat_mode(dest/'compose-resolved.private.json'),0o600)
            with tarfile.open(dest/'data.tar') as archive:
                self.assertEqual(set(archive.getnames()),{'sem.db','sem.db-wal','keys/key.xml','images/avatar.png'})
                self.assertTrue(all(m.uid==1654 and m.mode==0o600 for m in archive))

    def test_truncated_backup_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            root=Path(root)
            def execute(args,output=None,timeout=900): Path(output).write_bytes(b'truncated')
            with patch('updater.run',execute),self.assertRaises(tarfile.ReadError):
                Docker({'compose_files':[],'env_file':None},root).backup({'Id':'c','Image':'sha256:'+'d'*64},root,{})


def stat_mode(path):return path.stat().st_mode & 0o777


class ApiChecks(unittest.TestCase):
    def setUp(self):
        self.c=good()
        self.tag='sem-cd-'+self.c['commit']+'-22-1'
        self.release=dict(tag_name=self.tag,prerelease=True,draft=False)
        self.base='/repos/'+REPOSITORY
        repo={'full_name':REPOSITORY}
        self.integration=dict(repository=repo,head_repository=repo,event='push',head_branch='main',
            status='completed',conclusion='success',workflow_id=8,head_sha=self.c['commit'],run_attempt=1)
        self.publication=dict(repository=repo,head_repository=repo,event='workflow_run',head_branch='main',
            status='completed',conclusion='success',workflow_id=9,head_sha=self.c['commit'],run_attempt=1)
        self.responses={
          self.base+'/actions/runs/11':self.integration,
          self.base+'/actions/runs/22':self.publication,
          self.base+'/actions/runs/11/attempts/1/jobs?per_page=100':dict(total_count=2,jobs=[dict(name=n,status='completed',conclusion='success') for n in ['integration-checks','acceptance-browser']]),
          self.base+'/actions/workflows/main-integration.yml':dict(id=8,path='.github/workflows/main-integration.yml'),
          self.base+'/actions/workflows/cd-image.yml':dict(id=9,path='.github/workflows/cd-image.yml'),
          self.base+'/git/ref/heads/main':dict(object=dict(sha=self.c['commit'])),
          self.base+'/git/ref/tags/'+self.tag:dict(object=dict(type='commit',sha=self.c['commit']))}
        self.api=GitHub();self.api.get=lambda path:self.responses[path]

    def test_api_eligibility(self):self.assertTrue(self.api.verify(self.c,self.release))

    def test_publish_identity_and_status(self):
        for field,value in [('conclusion','failure'),('conclusion','skipped'),('event','pull_request'),
             ('head_sha','f'*40),('run_attempt',2),('head_branch','feature'),('workflow_id',100),('status','in_progress')]:
            original=self.publication[field];self.publication[field]=value
            with self.subTest(field=field),self.assertRaises(CDException):self.api.verify(self.c,self.release)
            self.publication[field]=original

    def test_integration_identity(self):
        for field,value in [('head_sha','f'*40),('run_attempt',2)]:
            original=self.integration[field];self.integration[field]=value
            with self.assertRaises(CDException):self.api.verify(self.c,self.release)
            self.integration[field]=original

    def test_tag_not_name_only(self):
        ref=self.responses[self.base+'/git/ref/tags/'+self.tag]['object']
        for key,value in [('type','tag'),('sha','f'*40)]:
            old=ref[key];ref[key]=value
            with self.assertRaises(CDException):self.api.verify(self.c,self.release)
            ref[key]=old

    def test_release_invalid(self):
        for key,value in [('tag_name','sem-cd-other'),('draft',True),('prerelease',False)]:
            release={**self.release,key:value}
            with self.assertRaises(CDException):self.api.verify(self.c,release)

    def test_stale_main(self):
        self.responses[self.base+'/git/ref/heads/main']['object']['sha']='f'*40
        self.assertFalse(self.api.verify(self.c,self.release))

    def test_install_interpolation(self):
        from initialize import escape_dollars
        value={'environment':{'SMTP_PASSWORD':'synthetic$literal${NOT_AN_ENV}'},'ports':['8080']}
        result=escape_dollars(value)
        self.assertEqual(result['environment']['SMTP_PASSWORD'],'synthetic$$literal$${NOT_AN_ENV}')
        self.assertEqual(value['environment']['SMTP_PASSWORD'],'synthetic$literal${NOT_AN_ENV}')


if __name__=='__main__': unittest.main()
