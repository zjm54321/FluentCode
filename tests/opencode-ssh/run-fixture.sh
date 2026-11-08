#!/usr/bin/env bash
# Opt-in live test: owns one temporary user-level sshd. Never changes the real OpenCode service or ~/.ssh.
set -euo pipefail
repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
fixture=/tmp/opencode/ssh-fixture
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
windows_fixture="$repo/artifacts/sshint-fixture space's-$stamp"
evidence="$repo/artifacts/ssh-integrate-live-$stamp"
sshd_log=/tmp/opencode/sshint-fixture-sshd.log
pwsh='/mnt/c/Program Files/PowerShell/7/pwsh.exe'
if [[ -e "$fixture" || -e "$windows_fixture" ]]; then
    printf 'Refusing to reuse an existing fixture directory.\n' >&2
    exit 1
fi
if [[ -n "$(ss -ltnH 'sport = :2222')" ]]; then
    printf 'Port 2222 is already occupied; no existing listener was touched.\n' >&2
    exit 1
fi
sshd_bin="$(command -v sshd)"
keygen="$(command -v ssh-keygen)"
real_opencode="$(command -v opencode)"
remote_user="$(id -un)"
umask 077
mkdir -- "$fixture"
printf '%s\n' "$$" > "$fixture/owner"
sshd_pid=
cleanup() {
    local result=$?
    trap - EXIT
    trap '' INT TERM
    if [[ -f "$fixture/sshd.pid" && -f "$fixture/stop-fixture.sh" ]]; then
        sh "$fixture/stop-fixture.sh" "$(<"$fixture/sshd.pid")" || result=1
    fi
    if [[ -n "$sshd_pid" ]]; then wait "$sshd_pid" 2>/dev/null || true; fi
    # Both directories were created by this invocation; never remove a pre-existing fixture or user SSH directory.
    if [[ -f "$fixture/owner" && "$(<"$fixture/owner")" == "$$" ]]; then rm -rf -- "$fixture"; fi
    if [[ "$windows_fixture" == "$repo/artifacts/sshint-fixture space's-$stamp" && -d "$windows_fixture" ]]; then
        "$pwsh" -NoProfile -NonInteractive -File "$(wslpath -w "$repo/tests/opencode-ssh/cleanup-fixture.ps1")" -FixtureDirectory "$(wslpath -w "$windows_fixture")" >/tmp/opencode/sshint-fixture-cleanup.log 2>&1 || result=1
    fi
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
"$keygen" -q -t ed25519 -N '' -f "$fixture/host-key"
"$keygen" -q -t ed25519 -N '' -f "$fixture/client-key"
printf 'restrict,port-forwarding,command="%s/remote-shell.sh" %s\n' "$fixture" "$(<"$fixture/client-key.pub")" > "$fixture/authorized_keys"
printf '%s\n' "$real_opencode" > "$fixture/real-opencode"
printf '%s\n' "$sshd_bin" > "$fixture/sshd-executable"
cp -- "$repo/tests/opencode-ssh/stop-fixture.sh" "$repo/tests/opencode-ssh/rekey-fixture.sh" "$fixture/"
mkdir -- "$fixture/bin"
cat > "$fixture/remote-shell.sh" <<'SH'
#!/bin/sh
export PATH="/tmp/opencode/ssh-fixture/bin:/etc/profiles/per-user/$USER/bin:/run/current-system/sw/bin:$PATH"
case "$SSH_ORIGINAL_COMMAND" in
    true) exit 0 ;;
    'sh -s') exec sh -s ;;
    *) exit 126 ;;
esac
SH
cat > "$fixture/bin/opencode" <<'SH'
#!/bin/sh
set -eu
case "$*" in
    'service status')
        printf 'status\n' >> /tmp/opencode/ssh-fixture/shim-calls
        if [ -f /tmp/opencode/ssh-fixture/started ]; then printf 'http://127.0.0.1:49375\n'; else printf 'stopped\n'; fi ;;
    'service start')
        printf 'start\n' >> /tmp/opencode/ssh-fixture/shim-calls
        : > /tmp/opencode/ssh-fixture/started
        printf 'http://127.0.0.1:49375\n' ;;
    'service get hostname') printf '127.0.0.1\n' ;;
    'service get remote') printf 'false\n' ;;
    'service get env') printf '{}\n' ;;
    'service get password')
        printf 'password\n' >> /tmp/opencode/ssh-fixture/shim-calls
        IFS= read -r real < /tmp/opencode/ssh-fixture/real-opencode
        exec "$real" service get password ;;
    *) exit 64 ;;
esac
SH
chmod 700 -- "$fixture/remote-shell.sh" "$fixture/bin/opencode"
cp -- "$fixture/bin/opencode" "$fixture/bin/opencode o'connor"
cat > "$fixture/sshd_config" <<EOF
ListenAddress 127.0.0.1
Port 2222
HostKey $fixture/host-key
PidFile $fixture/sshd.pid
AuthorizedKeysFile $fixture/authorized_keys
StrictModes no
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
PermitRootLogin no
AllowUsers $remote_user
AllowTcpForwarding local
AllowAgentForwarding no
X11Forwarding no
PermitTTY no
PermitUserRC no
LogLevel VERBOSE
PerSourcePenalties no
EOF
"$sshd_bin" -t -f "$fixture/sshd_config"
setsid "$sshd_bin" -D -e -f "$fixture/sshd_config" -o "PidFile=$fixture/sshd.pid" > "$sshd_log" 2>&1 &
sshd_pid=$!
ready=false
for ((attempt=0; attempt<50; attempt++)); do
    if ! kill -0 "$sshd_pid" 2>/dev/null; then tail -n 25 "$sshd_log"; exit 1; fi
    if [[ -n "$(ss -ltnH 'sport = :2222')" ]]; then ready=true; break; fi
    sleep 0.1
done
if [[ "$ready" != true ]]; then printf 'Fixture sshd did not listen within 5 seconds.\n' >&2; exit 1; fi
sshd_pid="$(<"$fixture/sshd.pid")"
fixture_win="$(wslpath -w "$windows_fixture")"
evidence_win="$(wslpath -w "$evidence")"
source_win="$(wslpath -w "$fixture")"
prepare_win="$(wslpath -w "$repo/tests/opencode-ssh/prepare-fixture.ps1")"
runner_win="$(wslpath -w "$repo/tests/opencode-ssh/run.ps1")"
"$pwsh" -NoProfile -NonInteractive -File "$prepare_win" \
    -FixtureDirectory "$fixture_win" -SourceDirectory "$source_win" -RemoteUser "$remote_user"
"$pwsh" -NoProfile -NonInteractive -File "$(wslpath -w "$repo/tests/opencode-ssh/invoke-fixture.ps1")" \
    -EvidenceRoot "$evidence_win" -FixtureRoot "$fixture_win" -FixturePid "$sshd_pid"
printf 'Live SSH evidence: %s\n' "$evidence"
cp -- "$fixture/shim-calls" "$evidence/shim-calls.txt"
printf '%s\n' "$(date -u +%FT%TZ): SSH integration fixture passed; fixture removed on exit." >> /tmp/opencode/sshint-progress.md
