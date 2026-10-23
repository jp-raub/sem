#!/usr/bin/env python3
"""Invoke once from a known-existing tool directory, never from an assumed cwd."""
import argparse
from pathlib import Path
import subprocess

def run(workspace, command, invoke=subprocess.run):
    if not Path(workspace).is_dir():
        print('workspace-missing: recover/inspect before executing; nothing retried')
        return 2
    if not command:
        print('command-required')
        return 2
    try:
        return invoke(command, cwd=workspace, check=False).returncode
    except OSError:
        print('workspace-command-start-failed: inspect state before any retry')
        return 2

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workspace',required=True)
    parser.add_argument('command',nargs=argparse.REMAINDER)
    args=parser.parse_args()
    command=args.command[1:] if args.command[:1]==['--'] else args.command
    return run(args.workspace,command)

if __name__=='__main__':raise SystemExit(main())
