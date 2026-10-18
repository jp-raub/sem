#!/usr/bin/env python3
"""Host-side SEM-only pull CD. Standard library; no remote shell commands.

The root-owned local installation/configuration is the trust boundary. Docker access
is host-administrative access, not a per-container sandbox.
"""
import argparse
import contextlib
import datetime
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import time
import urllib.error
import urllib.request
import urllib.parse

from release_gate import REPOSITORY, WORKFLOW, validate

IMAGE = 'ghcr.io/jp-raub/sem'
PUBLICATION_WORKFLOW = '.github/workflows/cd-image.yml'
CONFIG = Path('/etc/sem-cd/config.json')
SHA = re.compile(r'[0-9a-f]{40}')
DIGEST = re.compile(re.escape(IMAGE) + r'@sha256:[0-9a-f]{64}')
CANDIDATE_KEYS = {'deployment_mode', 'schema', 'repository', 'commit', 'image', 'platform', 'integration_run',
                  'integration_attempt', 'publication_run', 'publication_attempt'}


class CDException(Exception):
    """Only fixed messages reach the journal; private CLI output never does."""


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def atomic_json(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + '.tmp')
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'w') as handle:
        json.dump(value, handle, indent=2, sort_keys=True)
        handle.write('\n')
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temporary, path)
    fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def read_json(path, default):
    if not path.exists():
        return default
    try:
        return json.loads(path.read_text())
    except (ValueError, OSError):
        raise CDException('Local state is unreadable; inspect before retry') from None


def secure_path(path, directory=False):
    """Reject symlinks and non-root/writable ancestors on the privileged path."""
    path = Path(path)
    if not path.is_absolute():
        raise CDException('Expected absolute local path')
    for part in [path, *path.parents]:
        s = part.lstat()
        if stat.S_ISLNK(s.st_mode) or s.st_uid != 0 or s.st_mode & 0o022:
            raise CDException('Privileged path must be root-owned and not group/world writable')
    if directory and not path.is_dir():
        raise CDException('Expected directory')
    if not directory and not path.is_file():
        raise CDException('Expected regular file')
    return path


def candidate(raw):
    if type(raw) is not dict or set(raw) != CANDIDATE_KEYS:
        raise CDException('Invalid candidate keys')
    if (type(raw['schema']) is not int or raw['schema'] != 1 or raw['repository'] != REPOSITORY
        or raw.get('deployment_mode') not in {'auto', 'manual'}
        or raw['platform'] != 'linux/amd64' or type(raw['commit']) is not str
        or not SHA.fullmatch(raw['commit']) or type(raw['image']) is not str
        or not DIGEST.fullmatch(raw['image'])):
        raise CDException('Invalid candidate identity')
    for key in ('integration_run', 'integration_attempt', 'publication_run', 'publication_attempt'):
        if type(raw[key]) is not int or raw[key] < 1:
            raise CDException('Invalid candidate run identity')
    return raw


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise CDException('Unexpected HTTP redirect')


class GitHub:
    def __init__(self, token=None):
        self.token = token
        self.opener = urllib.request.build_opener(NoRedirect)

    def get(self, path):
        # Public repository / public GHCR require no long-lived GitHub credential.
        if not path.startswith('/repos/' + REPOSITORY + '/'):
            raise CDException('Unexpected API path')
        headers = {'Accept': 'application/vnd.github+json', 'User-Agent': 'sem-dxp-cd',
                   'X-GitHub-Api-Version': '2022-11-28'}
        if self.token:
            headers['Authorization'] = 'Bearer ' + self.token
        try:
            with self.opener.open(urllib.request.Request('https://api.github.com' + path,
                                                        headers=headers), timeout=30) as response:
                data = response.read(2 * 1024 * 1024 + 1)
                if len(data) > 2 * 1024 * 1024:
                    raise CDException('API response limit exceeded')
                return json.loads(data)
        except (urllib.error.URLError, ValueError):
            raise CDException('GitHub request failed; application unchanged') from None

    def download_candidate(self, url):
        # Release download redirects to GitHub's asset CDN. This request has NO token.
        if not re.fullmatch(r'https://github\.com/jp-raub/sem/releases/download/sem-cd-[a-z0-9-]+/candidate\.json', url):
            raise CDException('Invalid release asset URL')
        try:
            with urllib.request.urlopen(url, timeout=30) as response:
                final = urllib.parse.urlparse(response.geturl())
                if final.scheme != 'https' or final.hostname not in {'github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com'}:
                    raise CDException('Unexpected asset host')
                raw = response.read(8193)
                if len(raw) > 8192:
                    raise CDException('Candidate exceeds limit')
                return candidate(json.loads(raw))
        except (urllib.error.URLError, ValueError):
            raise CDException('Candidate download failed') from None

    def current_main(self):
        return self.get('/repos/' + REPOSITORY + '/git/ref/heads/main')['object']['sha']

    def verify(self, c, release):
        base = '/repos/' + REPOSITORY
        tag = f'sem-cd-{c["commit"]}-{c["publication_run"]}-{c["publication_attempt"]}'
        if release.get('tag_name') != tag or release.get('draft') or not release.get('prerelease'):
            raise CDException('Candidate release identity mismatch')
        integration = self.get(f'{base}/actions/runs/{c["integration_run"]}')
        if (integration.get('head_sha') != c['commit'] or integration.get('run_attempt') != c['integration_attempt']):
            raise CDException('Integration identity mismatch')
        jobs = self.get(f'{base}/actions/runs/{c["integration_run"]}/attempts/{c["integration_attempt"]}/jobs?per_page=100')
        if jobs['total_count'] != len(jobs['jobs']):
            raise CDException('Truncated jobs')
        workflow = self.get(base + '/actions/workflows/main-integration.yml')
        try:
            if not validate(integration, jobs['jobs'], workflow, self.current_main()):
                return False
        except ValueError:
            raise CDException('Integration is not eligible') from None
        publication = self.get(f'{base}/actions/runs/{c["publication_run"]}')
        workflow = self.get(base + '/actions/workflows/cd-image.yml')
        # Bind the successful publisher run to the same source as the CI and tag.
        if (workflow.get('path') != PUBLICATION_WORKFLOW or publication.get('workflow_id') != workflow.get('id')
            or publication.get('event') != 'workflow_run' or publication.get('head_branch') != 'main'
            or publication.get('head_sha') != c['commit']
            or publication.get('repository', {}).get('full_name') != REPOSITORY
            or publication.get('head_repository', {}).get('full_name') != REPOSITORY
            or publication.get('run_attempt') != c['publication_attempt']
            or publication.get('status') != 'completed' or publication.get('conclusion') != 'success'):
            raise CDException('Publication is not successful')
        # The release tag must resolve to the verified source, not merely be named after it.
        ref = self.get(base + '/git/ref/tags/' + tag)['object']
        if ref.get('type') != 'commit' or ref.get('sha') != c['commit']:
            raise CDException('Candidate tag does not identify source commit')
        return True

    def latest(self):
        base = '/repos/' + REPOSITORY
        current = self.current_main()
        # Ignore normal human releases; bound both pages and frequency. No mutable latest tag.
        for page in range(1, 4):
            releases = self.get(f'{base}/releases?per_page=30&page={page}')
            for release in releases:
                if not release.get('tag_name', '').startswith('sem-cd-' + current + '-') or release.get('draft'):
                    continue
                assets = [a for a in release.get('assets', []) if a.get('name') == 'candidate.json']
                if len(assets) != 1 or assets[0].get('size', 8193) > 8192:
                    raise CDException('Missing or invalid candidate asset')
                c = self.download_candidate(assets[0]['browser_download_url'])
                if c['commit'] != current:
                    raise CDException('Candidate source mismatch')
                # Publication may still be completing after release asset upload. Wait
                # until the next timer tick, not an error or a deploy of a partial run.
                pub = self.get(f'{base}/actions/runs/{c["publication_run"]}')
                if pub.get('status') != 'completed':
                    return None
                return c if self.verify(c, release) else None
            if len(releases) < 30:
                break
        return None


def run(args, output=None, timeout=900):
    try:
        if output is None:
            result = subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    timeout=timeout)
            return result.stdout
        # Preserve binary archives on disk; do not buffer an image/data archive in memory.
        with open(output, 'xb') as handle:
            subprocess.run(args, check=True, stdout=handle, stderr=subprocess.PIPE, timeout=timeout)
        os.chmod(output, 0o600)
        return b''
    except (subprocess.SubprocessError, OSError):
        raise CDException('Local container operation failed (private output withheld)') from None


def hash_file(path):
    result = hashlib.sha256()
    with open(path, 'rb') as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b''):
            result.update(chunk)
    return result.hexdigest()


class Docker:
    def __init__(self, config, state):
        self.config, self.state = config, state

    def compose(self, *args, override=False):
        c = self.config
        command = ['docker', 'compose', '--project-name', c['project'], '--project-directory', c['project_directory']]
        for file in c['compose_files']:
            command += ['-f', file]
        if c.get('env_file'):
            command += ['--env-file', c['env_file']]
        if override:
            command += ['-f', str(self.state / 'image.override.json')]
        return run(command + list(args))

    def container(self):
        ids = self.compose('ps', '-a', '-q', self.config['service']).decode().split()
        if len(ids) != 1 or not re.fullmatch('[a-f0-9]{12,64}', ids[0]):
            raise CDException('Expected exactly one existing SEM container')
        container = json.loads(run(['docker', 'inspect', ids[0]]))[0]
        self.check_container(container)
        return container

    def check_container(self, container):
        labels = container['Config'].get('Labels') or {}
        c = self.config
        if labels.get('com.docker.compose.project') != c['project'] or labels.get('com.docker.compose.service') != c['service']:
            raise CDException('Unexpected compose identity')
        mounts = [m for m in container['Mounts'] if m['Destination'] == '/data']
        if len(mounts) != 1:
            raise CDException('Expected exactly one /data mount')
        mount = mounts[0]
        actual = mount.get('Name') if mount['Type'] == 'volume' else mount['Source']
        if mount['Type'] != c['data_type'] or actual != c['data_source'] or not mount.get('RW'):
            raise CDException('Persistent data mount mismatch')

    def pull(self, c):
        run(['docker', 'pull', '--platform', 'linux/amd64', c['image']])
        image = json.loads(run(['docker', 'image', 'inspect', c['image']]))[0]
        self.check_image(image, c)
        return image

    @staticmethod
    def check_image(image, c):
        labels = image['Config'].get('Labels') or {}
        if (image.get('Os') != 'linux' or image.get('Architecture') != 'amd64'
            or image['Config'].get('User') != 'app' or labels.get('org.opencontainers.image.revision') != c['commit']
            or labels.get('org.opencontainers.image.source') != 'https://github.com/' + REPOSITORY
            or c['image'] not in image.get('RepoDigests', [])):
            raise CDException('Pulled image metadata mismatch')

    def is_current(self, c):
        current = self.container()
        if not current['State']['Running']:
            raise CDException('Recorded SEM is no longer running; inspect locally')
        image = json.loads(run(['docker', 'image', 'inspect', current['Image']]))[0]
        self.check_image(image, c)
        return True

    def preflight(self, old, image, backup_root):
        if not old['State']['Running']:
            raise CDException('Existing SEM is not running; inspect before update')
        # Only estimate application data size; never print paths/files or credentials.
        usage = run(['docker', 'exec', old['Id'], 'du', '-sk', '/data']).decode().split()[0]
        size = int(usage) * 1024
        old_image = json.loads(run(['docker', 'image', 'inspect', old['Image']]))[0]
        need = size * 2 + int(old_image['Size']) * 2 + 512 * 1024 * 1024
        if shutil.disk_usage(backup_root).free < need:
            raise CDException('Insufficient backup space; application unchanged')
        # Resolve merged configuration before stopping; never echo its secret contents.
        effective = json.loads(self.compose('config', '--format', 'json'))
        service = effective['services'][self.config['service']]
        if service.get('privileged') or service.get('volumes_from'):
            raise CDException('Unsupported privileged or inherited-volume service')
        data = [v for v in service.get('volumes', []) if v.get('target') == '/data']
        if len(data) != 1 or data[0].get('read_only'):
            raise CDException('Compose /data mount is missing or read-only')
        desired = data[0]
        kind = desired.get('type')
        source = desired.get('source')
        if kind == 'volume':
            source = effective.get('volumes', {}).get(source, {}).get('name')
        if kind != self.config['data_type'] or source != self.config['data_source']:
            raise CDException('Compose would change the persistent data mount')
        return effective

    def stop(self, old):
        run(['docker', 'stop', '--time', '60', old['Id']])
        actual = json.loads(run(['docker', 'inspect', old['Id']]))[0]
        if actual['State']['Running']:
            raise CDException('Container did not stop')

    def backup(self, old, directory, effective):
        run(['docker', 'image', 'save', old['Image']], directory / 'old-image.tar')
        run(['docker', 'cp', '-a', old['Id'] + ':/data/.', '-'], directory / 'data.tar')
        # Traverse all archive payloads to detect truncated files; preserve symlinks,
        # ownership and modes in the original tar. Do not extract private data here.
        with tarfile.open(directory / 'data.tar', 'r:') as archive:
            entries = 0
            for member in archive:
                if member.name.startswith('/') or '..' in Path(member.name).parts:
                    raise CDException('Unexpected backup archive path')
                entries += 1
                if member.isfile():
                    with archive.extractfile(member) as handle:
                        while handle.read(1024 * 1024):
                            pass
            if not entries:
                raise CDException('Empty data archive')
        atomic_json(directory / 'compose-resolved.private.json', effective)
        atomic_json(directory / 'container.private.json', old)
        for i, file in enumerate(self.config['compose_files'] + ([self.config['env_file']] if self.config.get('env_file') else [])):
            target = directory / ('local-config-' + str(i))
            shutil.copyfile(file, target)
            target.chmod(0o600)
        manifest = {p.name: hash_file(p) for p in sorted(directory.iterdir()) if p.is_file()}
        atomic_json(directory / 'SHA256SUMS.json', manifest)
        if any(hash_file(directory / name) != digest for name, digest in manifest.items()):
            raise CDException('Backup hash mismatch')

    def restart_old(self, old):
        run(['docker', 'start', old['Id']])

    def activate(self, c):
        atomic_json(self.state / 'image.override.json', {'services': {self.config['service']: {'image': c['image']}}})
        self.compose('up', '-d', '--no-build', '--no-deps', '--pull', 'never', '--force-recreate',
                     self.config['service'], override=True)
        current = self.container()
        image = json.loads(run(['docker', 'image', 'inspect', c['image']]))[0]
        if current['Image'] != image['Id']:
            raise CDException('Running image does not match candidate')
        return current

    def healthy(self, current):
        # No dependency on host port/NPM. Read health from the existing bridge address.
        for _ in range(60):
            inspected = json.loads(run(['docker', 'inspect', current['Id']]))[0]
            if not inspected['State']['Running']:
                return False
            # Application images contain no curl. Use host Python through the container
            # network's own routable IP; the health endpoints intentionally allow HTTP.
            addresses = [n.get('IPAddress') for n in inspected['NetworkSettings']['Networks'].values() if n.get('IPAddress')]
            if not addresses:
                return False
            good = 0
            for _ in range(3):
                try:
                    for path in ('live', 'ready'):
                        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect)
                        with opener.open('http://' + addresses[0] + ':8080/health/' + path, timeout=3) as response:
                            if response.status != 200:
                                raise CDException('Health check failed')
                    good += 1
                    time.sleep(2)
                except (urllib.error.URLError, OSError, CDException):
                    break
            if good == 3:
                return True
            time.sleep(2)
        return False

    def stop_failed(self):
        # Stop the candidate even when a restart policy would otherwise loop it.
        self.compose('stop', '--timeout', '30', self.config['service'], override=True)


class Updater:
    def __init__(self, config, state, source=None, docker=None):
        self.config, self.root = config, Path(state)
        self.source = source or GitHub()
        self.docker = docker or Docker(config, self.root)
        self.path = self.root / 'status.json'

    def state(self):
        value = read_json(self.path, {'paused': True, 'blocked': False, 'phase': 'not-configured'})
        if type(value) is not dict or type(value.get('paused')) is not bool or type(value.get('blocked')) is not bool:
            raise CDException('Invalid local status')
        return value

    def save(self, state, **changes):
        state.update(changes, updated_at=now())
        atomic_json(self.path, state)

    def tick(self):
        state = self.state()
        if state['paused'] or state['blocked']:
            return
        # A process killed/power lost after intent-to-stop must never automatically
        # replay the deployment. Durable write precedes any destructive operation.
        if state.get('phase') in {'stopping', 'backup', 'activating', 'health'}:
            self.save(state, blocked=True, phase='interrupted', message='Interrupted update; inspect backup and runtime')
            return
        try:
            # One anonymous API request for an already deployed main. Revalidating all
            # run/release metadata every five minutes would exceed the public 60/h limit.
            if state.get('current_commit') == self.source.current_main() and state.get('current_image'):
                self.docker.is_current({'commit': state['current_commit'], 'image': state['current_image']})
                return
            c = self.source.latest()
            if c is None:
                return
            c = candidate(c)
            if state.get('current_image') == c['image']:
                return
            if c['deployment_mode'] == 'manual':
                self.save(state, phase='manual-required', paused=True, candidate_commit=c['commit'],
                          candidate_image=c['image'], message='Special update requires local review; no app changes')
                return
            self.execute(c, state)
        except Exception as error:
            # Never log subprocess output, HTTP response bodies or arbitrary exceptions.
            self.save(state, phase='preflight-failed', message=str(error) if isinstance(error, CDException) else 'Preflight failed; private details withheld')

    def execute(self, c, state):
        old = self.docker.container()
        image = self.docker.pull(c)
        root = Path(self.config['backup_root'])
        effective = self.docker.preflight(old, image, root)
        # Download may take minutes. Do not stop an app for a superseded candidate.
        if self.source.current_main() != c['commit']:
            self.save(state, phase='superseded', candidate_commit=c['commit'])
            return
        directory = root / (datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + c['commit'][:12])
        directory.mkdir(mode=0o700)
        self.save(state, phase='stopping', blocked=True, candidate_commit=c['commit'], candidate_image=c['image'], backup=str(directory))
        activated = False
        try:
            self.docker.stop(old)
            self.save(state, phase='backup')
            self.docker.backup(old, directory, effective)
            # No old-image-only rollback once Compose/new process might have run.
            self.save(state, phase='activating')
            activated = True
            current = self.docker.activate(c)
            self.save(state, phase='health')
            if not self.docker.healthy(current):
                raise CDException('New application health failed')
            self.save(state, phase='ready', blocked=False, current_commit=c['commit'], current_image=c['image'], message='Update and health checks succeeded')
        except Exception:
            if activated:
                try:
                    self.docker.stop_failed()
                except Exception:
                    pass
            else:
                try:
                    self.docker.restart_old(old)
                except Exception:
                    pass
            self.save(state, phase='failed-after-start' if activated else 'failed-before-start', blocked=True,
                      message='Update failed; inspect locally before recovery; no automatic downgrade')


def load_config(path):
    secure_path(path)
    c = read_json(path, {})
    required = {'project', 'service', 'project_directory', 'compose_files', 'env_file', 'data_type', 'data_source', 'backup_root', 'state_directory'}
    if set(c) != required or c['service'] != 'sem' or not re.fullmatch('[a-z0-9][a-z0-9_-]{0,62}', c['project']):
        raise CDException('Unsupported local configuration')
    if c['data_type'] not in {'volume', 'bind'} or not c['data_source']:
        raise CDException('Unsupported persistent data configuration')
    for path in c['compose_files'] + ([c['env_file']] if c['env_file'] else []):
        secure_path(path)
    for key in ('backup_root', 'state_directory'):
        secure_path(c[key], directory=True)
    if c['data_type'] == 'bind':
        if Path(c['backup_root']).resolve().is_relative_to(Path(c['data_source']).resolve()):
            raise CDException('Backup must not be inside application data')
    return c


@contextlib.contextmanager
def locked(root):
    fd = os.open(Path(root) / 'update.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise CDException('Another update/control operation is in progress') from None
        yield
    finally:
        os.close(fd)


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description='SEM pull CD (root-owned fixed configuration)')
    parser.add_argument('action', choices=['tick', 'status', 'pause', 'resume', 'acknowledge-failure'])
    parser.add_argument('--config', type=Path, default=CONFIG)
    args = parser.parse_args()
    if os.geteuid() != 0:
        raise CDException('Run using the installed root-owned command')
    config = load_config(args.config)
    with locked(config['state_directory']):
        updater = Updater(config, config['state_directory'])
        state = updater.state()
        if args.action == 'tick':
            updater.tick()
        elif args.action == 'pause':
            updater.save(state, paused=True)
        elif args.action == 'resume':
            updater.save(state, paused=False) # Does not clear a failed/interrupted update.
        elif args.action == 'acknowledge-failure':
            if not state['paused']:
                raise CDException('Pause and inspect/recover locally before acknowledging failure')
            updater.save(state, blocked=False, phase='acknowledged', message='Failure acknowledged while paused')
        # Fixed safe fields only. Never include local configuration, env or container inspect.
        safe = updater.state()
        if args.action == 'status':
            observed = updater.docker.container()
            revision = (observed['Config'].get('Labels') or {}).get('org.opencontainers.image.revision', '')
            safe['observed_running'] = bool(observed['State']['Running'])
            safe['observed_commit'] = revision if SHA.fullmatch(revision) else None
        fields = {'observed_running', 'observed_commit', 'paused', 'blocked', 'phase', 'updated_at', 'current_commit', 'current_image', 'candidate_commit', 'candidate_image', 'message'}
        print(json.dumps({k: v for k, v in safe.items() if k in fields}, indent=2))


if __name__ == '__main__':
    try:
        main()
    except CDException as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
