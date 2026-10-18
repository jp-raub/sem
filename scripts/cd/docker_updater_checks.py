#!/usr/bin/env python3
"""Real Docker/Compose fixture for CI only; synthetic isolated resources.

Production has no local-image bypass. Only this test adapter supplies the PR-local
image instead of GHCR, so candidate/metadata API validation is tested separately.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import sys
import tarfile
import tempfile
import time

from updater import Docker, Updater, atomic_json, run, hash_file
from updater_checks import Source
from initialize import prepare, escape_dollars


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--image',required=True)
    image=parser.parse_args().image
    root=Path(tempfile.mkdtemp(prefix='sem-cd-docker-',dir='/root'))
    project='sem-cd-fixture-'+str(os.getpid())
    volume=project+'-data'
    restore_volume=project+'-restore'
    compose=root/'compose.json'
    command=['docker','compose','-p',project,'-f',str(compose)]
    stage='create fixture'
    try:
        run(['docker','volume','create',volume])
        run(['docker','volume','create',restore_volume])
        atomic_json(compose,escape_dollars({'services':{'sem':{'image':image,'volumes':[volume+':/data'],
                    'environment':{'Smtp__Password':'synthetic$literal${NOT_AN_ENV}'},
                    'restart':'unless-stopped','cap_drop':['ALL'],'security_opt':['no-new-privileges:true']}},
                    'volumes':{volume:{'external':True,'name':volume}}}))
        config=dict(project=project,service='sem',project_directory=str(root),compose_files=[str(compose)],
                    env_file=None,data_type='volume',data_source=volume,backup_root=str(root/'backups'),state_directory=str(root))
        (root/'backups').mkdir()
        engine=Docker(config,root)
        stage='start synthetic Compose service'
        run(command+['up','-d','--no-build'])
        stage='inspect synthetic Compose service'
        current=engine.container()
        stage='initial synthetic health'
        assert engine.healthy(current),'Initial synthetic application not ready'
        stage='seed synthetic data and key-directory file'
        # Health-only startup need not materialize a Data Protection key ring.
        # This non-key-*.xml fixture is deliberately not a usable authentication key.
        run(['docker','exec',current['Id'],'sh','-c',
             'printf synthetic-cd-data > /data/cd-sentinel; printf synthetic-key-file > /data/keys/cd-fixture.bin; '
             'chmod 600 /data/cd-sentinel /data/keys/cd-fixture.bin'])
        stage='hash seeded synthetic data and key-directory files'
        before=run(['docker','exec',current['Id'],'sh','-c','sha256sum /data/cd-sentinel /data/keys/*']).decode()
        # Exercise actual root-owned initial capture, including literal '$' secrets.
        stage='capture existing configuration'
        prepare([str(compose)],None,project,root/'backups',root/'config',root/'control')
        config=json.loads((root/'config'/'config.json').read_text())
        control=root/'control'
        captured=json.loads(Docker(config,control).compose('config','--format','json'))
        assert captured['services']['sem']['environment']['Smtp__Password']=='synthetic$literal${NOT_AN_ENV}'
        initial=json.loads((control/'status.json').read_text())
        assert initial['paused'] and not initial['blocked']
        engine=Docker(config,control)

        class LocalImageEngine(Docker):
            def pull(self,c):
                return json.loads(run(['docker','image','inspect',image]))[0]
            def activate(self,c):
                return super().activate({**c,'image':image})

        updater=Updater(config,control,Source(),LocalImageEngine(config,control))
        atomic_json(updater.path,dict(paused=False,blocked=False,phase='ready'))
        stage='update and preserve data'
        updater.tick()
        state=updater.state()
        assert state['phase']=='ready' and not state['blocked'],state['phase']+': '+state.get('message','')
        updated=engine.container()
        assert updated['Id']!=current['Id'],'Container was not regenerated'
        assert before==run(['docker','exec',updated['Id'],'sh','-c','sha256sum /data/cd-sentinel /data/keys/*']).decode()
        backup=Path(state['backup'])
        assert (backup/'old-image.tar').stat().st_size>0
        checks=json.loads((backup/'SHA256SUMS.json').read_text())
        assert all(hash_file(backup/name)==digest for name,digest in checks.items())
        with tarfile.open(backup/'data.tar') as archive:
            sentinel=next(m for m in archive if m.name.rstrip('/').endswith('cd-sentinel'))
            assert sentinel.mode & 0o777==0o600 and sentinel.uid!=0
            assert archive.extractfile(sentinel).read()==b'synthetic-cd-data'
        stage='restore backup into separate volume'
        # Restore stopped full data into a distinct empty volume, not back over source.
        run(['docker','run','--rm','--user','0','--network','none','--entrypoint','sh',
             '-v',restore_volume+':/restore','-v',str(backup)+':/backup:ro',image,'-c','tar -xf /backup/data.tar -C /restore'])
        restored=run(['docker','run','--rm','--user','0','--network','none','--entrypoint','sh',
             '-v',restore_volume+':/data:ro',image,'-c','sha256sum /data/cd-sentinel /data/keys/*']).decode()
        assert before==restored,'Restored sentinel/keys differ'
        stage='failed-health stop and no replay'
        # Simulated post-start failure must stop and block candidate; never replay on tick.
        class FailedHealth(LocalImageEngine):
            def healthy(self,c):return False
        # Avoid timestamp-directory collision while running a second synthetic update.
        time.sleep(1.1)
        updater.docker=FailedHealth(config,control)
        state['current_image']=None;atomic_json(updater.path,state)
        updater.tick()
        assert updater.state()['phase']=='failed-after-start' and updater.state()['blocked']
        assert not engine.container()['State']['Running']
        previous=updater.path.read_bytes()
        updater.tick()
        assert updater.path.read_bytes()==previous,'Blocked deployment was replayed'
        print('Real Docker: container regeneration, volume/key retention, archive hashes/ownership, isolated restore, failed-health stop/block, no replay: passed')
    except Exception as error:
        # Fixed stage and exception type only; no environment, logs or config.
        print('::error::Synthetic Docker fixture failed at '+stage+' ('+type(error).__name__+')'+(': '+str(error) if isinstance(error, __import__('updater').CDException) else ''),flush=True)
        raise
    finally:
        # Only this fixture project/volumes; production updater never runs down -v.
        try:run(command+['down','--remove-orphans'])
        finally:
            for name in (volume,restore_volume):
                try:run(['docker','volume','rm',name])
                except Exception:pass
            shutil.rmtree(root,ignore_errors=True)


if __name__=='__main__':main()
