#!/usr/bin/env bash
set -uo pipefail

floor="${GUARD_FLOOR_MIB:-350}"
[ "$#" -gt 0 ] || { echo "usage: bash deploy/guard.sh <command> [arguments]" >&2; exit 1; }

"$@" &
job=$!
lowest=999999
while kill -0 "$job" 2>/dev/null; do
    free="$(awk '/^MemFree:/{print int($2/1024)}' /proc/meminfo)"
    [ "$free" -lt "$lowest" ] && lowest="$free"
    if [ "$free" -lt "$floor" ]; then
        echo "guard: $free MiB free, below $floor MiB, stopping" >&2
        kill "$job" 2>/dev/null
        command -v dotnet >/dev/null 2>&1 && dotnet build-server shutdown >/dev/null 2>&1
        break
    fi
    sleep 2
done
wait "$job"
status=$?
echo "guard: exit $status, lowest free memory $lowest MiB" >&2
exit "$status"
