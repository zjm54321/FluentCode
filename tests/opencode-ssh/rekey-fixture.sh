#!/bin/sh
# Change only the disposable fixture host key, retain DEFAULT sshd KEX, then publish the new owned PID.
set -eu
fixture=/tmp/opencode/ssh-fixture
[ -f "$fixture/owner" ] || exit 1
[ -z "$(ss -ltnH 'sport = :2222')" ] || exit 1
mv -- "$fixture/host-key" "$fixture/host-key-old"
mv -- "$fixture/host-key.pub" "$fixture/host-key-old.pub"
ssh-keygen -q -t ed25519 -N '' -f "$fixture/host-key"
IFS= read -r sshd < "$fixture/sshd-executable"
nohup setsid "$sshd" -D -e -f "$fixture/sshd_config" -o "PidFile=$fixture/sshd.pid" </dev/null >/tmp/opencode/ssh-fixture-sshd-rekey.log 2>&1 &
pid=$!
attempt=0
while [ -z "$(ss -ltnH 'sport = :2222')" ]; do
    kill -0 "$pid" 2>/dev/null || exit 1
    attempt=$((attempt+1))
    [ "$attempt" -lt 50 ] || exit 1
    sleep 0.1
done
IFS= read -r pid < "$fixture/sshd.pid"
printf '%s\n' "$pid"
