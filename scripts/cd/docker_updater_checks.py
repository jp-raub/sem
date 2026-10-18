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


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--image',required=True)
    image=parser.parse_args().image
    root=Path(tempfile.mkdtemp(prefix='sem-cd-docker-'))
    project='sem-cd-fixture-'+str(os.getpid())
    volume=project+'-data'
    restore_volume=project+'-restore'
    compose=root/'compose.json'
    command=['docker','compose','-p',project,'-f',str(compose)]
    try:
        run(['docker','volume','create',volume])
        run(['docker','volume','create',restore_volume])
        atomic_json(compose,{'services':{'sem':{'image':image,'volumes':[volume+':/data'],
                    'restart':'unless-stopped','cap_drop':['ALL'],'security_opt':['no-new-privileges:true']}},
                    'volumes':{volume:{'external':True,'name':volume}}})
        config=dict(project=project,service='sem',project_directory=str(root),compose_files=[str(compose)],
                    env_file=None,data_type='volume',data_source=volume,backup_root=str(root/'backups'),state_directory=str(root))
        (root/'backups').mkdir()
        engine=Docker(config,root)
        run(command+['up','-d','--no-build'])
        current=engine.container()
        assert engine.healthy(current),'Initial synthetic application not ready'
        run(['docker','exec','--user','0',current['Id'],'sh','-c',
             'printf synthetic-cd-data > /data/cd-sentinel; chown app:app /data/cd-sentinel; chmod 600 /data/cd-sentinel'])
        before=run(['docker','exec',current['Id'],'sh','-c','sha256sum /data/cd-sentinel /data/keys/*']).decode()

        class LocalImageEngine(Docker):
            def pull(self,c):
                return json.loads(run(['docker','image','inspect',image]))[0]
            def activate(self,c):
                return super().activate({**c,'image':image})

        updater=Updater(config,root,Source(),LocalImageEngine(config,root))
        atomic_json(updater.path,dict(paused=False,blocked=False,phase='ready'))
        updater.tick()
        state=updater.state()
        assert state['phase']=='ready' and not state['blocked'],state['phase']
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
        # Restore stopped full data into a distinct empty volume, not back over source.
        run(['docker','run','--rm','--user','0','--network','none','--entrypoint','sh',
             '-v',restore_volume+':/restore','-v',str(backup)+':/backup:ro',image,'-c','tar -xf /backup/data.tar -C /restore'])
        restored=run(['docker','run','--rm','--user','0','--network','none','--entrypoint','sh',
             '-v',restore_volume+':/data:ro',image,'-c','sha256sum /data/cd-sentinel /data/keys/*']).decode()
        assert before==restored,'Restored sentinel/keys differ'
        # Simulated post-start failure must stop and block candidate; never replay on tick.
        class FailedHealth(LocalImageEngine):
            def healthy(self,c):return False
        # Avoid timestamp-directory collision while running a second synthetic update.
        time.sleep(1.1)
        updater.docker=FailedHealth(config,root)
        state['current_image']=None;atomic_json(updater.path,state)
        updater.tick()
        assert updater.state()['phase']=='failed-after-start' and updater.state()['blocked']
        assert not engine.container()['State']['Running']
        previous=updater.path.read_bytes()
        updater.tick()
        assert updater.path.read_bytes()==previous,'Blocked deployment was replayed'
        print('Real Docker: container regeneration, volume/key retention, archive hashes/ownership, isolated restore, failed-health stop/block, no replay: passed')
    finally:
        # Only this fixture project/volumes; production updater never runs down -v.
        try:run(command+['down','--remove-orphans'])
        finally:
            for name in (volume,restore_volume):
                try:run(['docker','volume','rm',name])
                except Exception:pass
            shutil.rmtree(root,ignore_errors=True)


if __name__=='__main__':main()
