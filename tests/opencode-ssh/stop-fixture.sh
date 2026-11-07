#!/bin/sh
# Stop ONLY our listener, by its own PidFile. Never enumerate processes, descendants or process groups.
set -eu
fixture=/tmp/opencode/ssh-fixture
case "${1:-}" in ''|*[!0-9]*) exit 1 ;; esac
[ -f "$fixture/sshd.pid" ] || exit 0
IFS= read -r active < "$fixture/sshd.pid"
[ "$1" = "$active" ] || exit 1
[ -f "$fixture/owner" ] || exit 1
if [ ! -r "/proc/$active/cmdline" ]; then exit 0; fi
command=$(tr '\000' ' ' < "/proc/$active/cmdline")
case "$command" in *"$fixture/sshd_config"*) ;; '') exit 0 ;; *) exit 1 ;; esac
printf 'SIGNAL fixture sshd PID=%s\n' "$active" >&2
kill -TERM "$active" 2>/dev/null || true
