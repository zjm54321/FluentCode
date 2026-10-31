#!/usr/bin/env python3
"""Run the 26 critical suite entries sequentially with immutable-candidate logs."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import time

REPO = Path(__file__).resolve().parents[1]
PWSH = '/mnt/c/Program Files/PowerShell/7/pwsh.exe'
BASE_ROOTS = {'opencode-composer'}
RUNNERS = (
    'opencode-auth/run.ps1',
    'opencode-connection/run.ps1',
    'opencode-pairing/run.ps1',
    'opencode-event-sync/run.ps1',
    'message-pagination/run.ps1',
    'opencode-view-ack/run.ps1',
    'opencode-observations/run.ps1',
    'notification-form/run.ps1',
    'opencode-composer/run.ps1',
    'opencode-composer-hookup/run.ps1',
    'opencode-commands-attachments/run.ps1',
    'opencode-queued-withdraw/run.ps1',
    'opencode-plugin-mutations/run.ps1',
    'opencode-session-controls/run.ps1',
    'opencode-session-ui-state/run.ps1',
    'fluent-motion/run.ps1',
    'opencode-session-return/run.ps1',
    'opencode-capabilities/run.ps1',
    'opencode-browser-engine/run.ps1',
    'browser-runtime/run.ps1',
    'opencode-browser-settings/run.ps1',
    'desktop-bridge-qol/run.ps1',
    'opencode-persistent-pty/run.ps1',
    'opencode-persistent-pty-transport/run-backend.ps1',
)
NODE_TESTS = ('runtime.test.mjs', 'audit.test.mjs')


def windows(path):
    return subprocess.check_output(['wslpath', '-w', str(path)], text=True).strip()


def quote(value):
    return "'" + value.replace("'", "''") + "'"


def parameter_block(text):
    match = re.search(r'(?im)^param\s*\(', text)
    if not match:
        return ''
    depth, quoted = 0, None
    for index in range(match.end() - 1, len(text)):
        char = text[index]
        if quoted:
            if char == quoted:
                quoted = None
        elif char in "\"'":
            quoted = char
        elif char == '(':
            depth += 1
        elif char == ')':
            depth -= 1
            if not depth:
                return text[match.start():index + 1]
    raise ValueError('Unterminated runner parameter block')


def candidate():
    names = subprocess.check_output(
        ['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard'], cwd=REPO
    ).split(b'\0')
    result = {}
    for name in set(filter(None, names)):
        relative = name.decode()
        path = REPO / relative
        if path.is_file():
            result[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def plan(root):
    entries = []
    for relative in RUNNERS:
        path = REPO / 'tests' / relative
        text = path.read_text(encoding='utf-8-sig')
        block = parameter_block(text)
        names = sorted(set(re.findall(r'\$(\w+)', block)))
        has_evidence = 'EvidenceRoot' in names or "Alias('EvidenceRoot')" in block
        identifier = path.parent.name + ('' if path.stem == 'run' else '-' + path.stem.removeprefix('run-'))
        leaf = root / 'evidence' / identifier
        args = '-EvidenceRoot ' + quote(windows(leaf)) if has_evidence else ''
        temporary = root / 'temp' / identifier
        command = ("$ErrorActionPreference='Stop'; "
                   "$env:Path='C:\\Program Files\\dotnet;'+$env:Path; "
                   "$env:TEMP=" + quote(windows(temporary)) + '; $env:TMP=$env:TEMP; '
                   'try { & ' + quote(windows(path)) + ' ' + args
                   + '; if (!$?) { exit 1 }; exit 0 } catch { Write-Error $_ -ErrorAction Continue; exit 1 }')
        entries.append({'id': identifier, 'runner': str(path.relative_to(REPO)),
                        'parameters': names, 'evidence': str(leaf), 'temp': str(temporary),
                        'precreate_evidence_base': path.parent.name in BASE_ROOTS,
                        'argv': [PWSH, '-NoProfile', '-NonInteractive', '-Command', command]})
    for name in NODE_TESTS:
        path = REPO / 'tests' / 'browser-runtime' / name
        if not path.exists():
            raise FileNotFoundError(path)
        identifier = 'browser-runtime-node-' + name.removesuffix('.test.mjs')
        temporary = root / 'temp' / identifier
        command = ("$ErrorActionPreference='Stop'; $env:TEMP=" + quote(windows(temporary))
                   + '; $env:TMP=$env:TEMP; & '
                   + quote(windows(REPO / 'artifacts/browser-runtime/node.exe'))
                   + ' --test ' + quote(windows(path)) + '; exit $LASTEXITCODE')
        entries.append({'id': identifier, 'runner': str(path.relative_to(REPO)),
                        'parameters': [], 'evidence': str(root / 'evidence' / identifier),
                        'temp': str(temporary), 'precreate_evidence_base': False,
                        'argv': [PWSH, '-NoProfile', '-NonInteractive', '-Command', command]})
    return entries


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--execute', action='store_true')
    args = parser.parse_args()
    root = args.root.resolve()
    if root.parent != REPO / 'artifacts' or not root.name.startswith(('prune-verify-', 'restoration-suite-')):
        parser.error('Use a NEW artifacts/prune-verify-* or artifacts/restoration-suite-* directory')
    entries = plan(root)
    if not args.execute:
        print(json.dumps(entries, ensure_ascii=False, indent=2))
        return 0
    root.mkdir(exist_ok=False)
    (root / 'logs').mkdir()
    (root / 'evidence').mkdir()
    before = candidate()
    (root / 'candidate.json').write_text(json.dumps(before, indent=2))
    (root / 'plan.json').write_text(json.dumps(entries, ensure_ascii=False, indent=2))
    results = []
    for entry in entries:
        Path(entry['temp']).mkdir(parents=True)
        if entry['precreate_evidence_base']:
            Path(entry['evidence']).mkdir(parents=True)
        start = time.monotonic()
        log = root / 'logs' / (entry['id'] + '.log')
        print('RUN', entry['id'], flush=True)
        with log.open('xb') as output:
            completed = subprocess.run(entry['argv'], cwd=REPO, stdout=output, stderr=subprocess.STDOUT)
        results.append({'id': entry['id'], 'exit_code': completed.returncode,
                        'seconds': round(time.monotonic() - start, 2), 'log': str(log)})
        (root / 'results.json').write_text(json.dumps(results, indent=2))
        print('RESULT', entry['id'], completed.returncode, flush=True)
        after = candidate()
        if before != after:
            changes = sorted(key for key in before.keys() | after.keys() if before.get(key) != after.get(key))
            (root / 'candidate-changes.json').write_text(json.dumps(changes, indent=2))
            print('STOP: candidate changed during suite:', changes, file=sys.stderr)
            return 2
    failures = [entry['id'] for entry in results if entry['exit_code']]
    print(json.dumps({'runners': len(results), 'failures': failures, 'root': str(root)}))
    return int(bool(failures))


if __name__ == '__main__':
    sys.exit(main())
