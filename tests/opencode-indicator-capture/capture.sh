#!/usr/bin/env bash
set -euo pipefail
if [[ $# != 2 ]]; then
    printf 'Usage: capture.sh <lane publish directory> <new evidence directory>\n' >&2
    exit 2
fi
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
publish=$(realpath -- "$1")
output=$(realpath -m -- "$2")
case "$publish" in "$repo"/*) ;; *) printf 'Publish directory must be inside this lane.\n' >&2; exit 2;; esac
case "$output" in "$repo"/artifacts/indicator-v3-capture-*) ;; *) printf 'Evidence must be a new indicator-v3-capture artifact in this lane.\n' >&2; exit 2;; esac
lock='/tmp/opencode/capture.lock'
deadline=$((SECONDS + 1200))
while ! mkdir "$lock" 2>/dev/null; do
    if ((SECONDS >= deadline)); then
        printf 'Capture lock unavailable after 20 minutes; no launch.\n' >&2
        exit 3
    fi
    printf 'Capture lock held; retrying in 15 s.\n'
    sleep 15
done
printf 'lane/indicator; shell PID=%s; UTC=%s; output=%s\n' "$$" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$output" > "$lock/owner"
trap 'rm -f -- "$lock/owner"; rmdir -- "$lock"' EXIT
printf 'Acquired shared capture lock; owner: lane/indicator PID=%s\n' "$$"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$repo/tests/opencode-indicator-capture/capture.ps1")" \
    -PublishDirectory "$(wslpath -w "$publish")" -EvidenceRoot "$(wslpath -w "$output")"
