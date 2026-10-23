#!/usr/bin/env python3
"""Restore an immutable SEM source snapshot without overwriting existing work."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import socket
import tarfile
import tempfile
import time
import urllib.error
import urllib.request

REPOSITORY = 'jp-raub/sem'
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_TREE = 128 * 1024 * 1024
MAX_FILE = 20 * 1024 * 1024
MAX_ENTRIES = 5000

class ResumeError(Exception):
    pass

def validate_commit(commit):
    if not isinstance(commit, str) or re.fullmatch(r'[0-9a-f]{40}', commit) is None:
        raise ResumeError('full-lowercase-commit-required')

def download(commit, opener=urllib.request.urlopen, sleep=time.sleep):
    validate_commit(commit)
    # Read-only GET only. No credentials, environment dump or response body in logs.
    request = urllib.request.Request(f'https://api.github.com/repos/{REPOSITORY}/tarball/{commit}',
                                     headers={'User-Agent': 'SEM-workspace-resume/1.0'})
    for attempt in range(3):
        try:
            with opener(request, timeout=30) as response:
                data = response.read(MAX_ARCHIVE + 1)
            if len(data) > MAX_ARCHIVE:
                raise ResumeError('archive-too-large')
            return data
        except urllib.error.HTTPError as exc:
            if exc.code not in (502, 503, 504) or attempt == 2:
                raise ResumeError(f'archive-http-{exc.code}') from None
        except (urllib.error.URLError, TimeoutError, socket.timeout):
            if attempt == 2:
                raise ResumeError('archive-network-unavailable') from None
        sleep(attempt + 1)
    raise ResumeError('archive-unavailable')

def unpack(data, destination):
    if len(data) > MAX_ARCHIVE:
        raise ResumeError('archive-too-large')
    try:
        with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as archive:
            entries = []
            seen = set()
            root = None
            total = 0
            for index, member in enumerate(archive):
                if index >= MAX_ENTRIES:
                    raise ResumeError('too-many-entries')
                path = PurePosixPath(member.name)
                if path.is_absolute() or '..' in path.parts or '\\' in member.name or not path.parts:
                    raise ResumeError('unsafe-archive-path')
                if root is None:
                    root = path.parts[0]
                if path.parts[0] != root:
                    raise ResumeError('multiple-archive-roots')
                if not (member.isfile() or member.isdir()) or member.issym() or member.islnk():
                    raise ResumeError('unsafe-archive-entry')
                relative = Path(*path.parts[1:])
                key = relative.as_posix()
                if key in seen:
                    raise ResumeError('duplicate-archive-path')
                seen.add(key)
                if not path.parts[1:]:
                    if not member.isdir():
                        raise ResumeError('invalid-archive-root')
                    continue
                total += member.size
                if member.size < 0 or member.size > MAX_FILE or total > MAX_TREE:
                    raise ResumeError('expanded-source-too-large')
                entries.append((member, relative))
            if not entries or not any(m.isfile() for m, _ in entries):
                raise ResumeError('empty-archive')
            for member, relative in entries:
                target = destination / relative
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with archive.extractfile(member) as source, target.open('xb') as output:
                        shutil.copyfileobj(source, output)
                    target.chmod(0o755 if member.mode & 0o111 else 0o644)
    except (tarfile.TarError, OSError, EOFError) as exc:
        raise ResumeError('invalid-archive') from exc

def inventory(root):
    result = {}
    for path in sorted(root.rglob('*')):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ResumeError('unexpected-workspace-entry')
        if path.is_file():
            result[path.relative_to(root).as_posix()] = {
                'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                'executable': bool(path.stat().st_mode & 0o111)}
    return result

def restore(commit, destination, fetch=download):
    validate_commit(commit)
    destination = Path(destination).absolute()
    checkpoint = destination.with_name(destination.name + '.resume.json')
    if destination.is_symlink() or checkpoint.is_symlink():
        raise ResumeError('symlink-destination')
    if destination.exists() or checkpoint.exists():
        if not destination.is_dir() or not checkpoint.is_file():
            raise ResumeError('incomplete-workspace-preserved')
        try:
            saved = json.loads(checkpoint.read_text())
        except (ValueError, OSError):
            raise ResumeError('invalid-checkpoint') from None
        if saved.get('schema') != 1 or saved.get('repository') != REPOSITORY or saved.get('commit') != commit:
            raise ResumeError('checkpoint-identity-mismatch')
        if inventory(destination) != saved.get('files'):
            raise ResumeError('workspace-changed-preserved')
        return 'verified-existing', saved
    destination.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix='.sem-resume-', dir=destination.parent))
    try:
        data = fetch(commit)
        tree = stage / 'source'
        tree.mkdir()
        unpack(data, tree)
        if not (tree / 'src/Sem.Web/Sem.Web.csproj').is_file():
            raise ResumeError('not-sem-source')
        saved = {'schema': 1, 'repository': REPOSITORY, 'commit': commit,
                 'archiveSha256': hashlib.sha256(data).hexdigest(), 'files': inventory(tree),
                 'tests': 'not-run'}
        record = stage / 'record.json'
        record.write_text(json.dumps(saved, sort_keys=True, indent=2) + '\n')
        # Refuse concurrent/dirty recovery rather than merge or delete local edits.
        # Lock is held for install only; archive GET/extraction have no external side effects.
        lock = destination.with_name(destination.name + '.resume.lock')
        try:
            descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        except FileExistsError:
            raise ResumeError('resume-lock-present') from None
        try:
            os.close(descriptor)
            if destination.exists() or checkpoint.exists() or destination.is_symlink() or checkpoint.is_symlink():
                raise ResumeError('destination-appeared-preserved')
            tree.rename(destination)
            record.rename(checkpoint)
        finally:
            lock.unlink()
        return 'restored', saved
    finally:
        shutil.rmtree(stage)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--destination', type=Path, required=True)
    args = parser.parse_args()
    try:
        outcome, saved = restore(args.commit, args.destination)
        print(json.dumps({'status': outcome, 'commit': saved['commit'],
                          'files': len(saved['files']), 'tests': 'not-run'}))
    except (ResumeError, OSError):
        # Fixed classification only: do not leak private paths, response bodies or credentials.
        print(json.dumps({'status': 'unconfirmed', 'reason': 'workspace-restore-failed',
                          'action': 'preserve-existing-work-and-inspect-locally'}))
        return 1
    return 0

if __name__ == '__main__':
    raise SystemExit(main())
