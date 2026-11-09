#!/usr/bin/env bash
set -euo pipefail
export MSYS_NO_PATHCONV=1

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
floor="${BUILD_FLOOR_MIB:-450}"
file="${COMPOSE_FILE_NAME:-docker-compose.prod.yml}"
services=("$@")
[ "${#services[@]}" -gt 0 ] || services=(caligo-ml caligo-client caligo-api)

windows() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi
}

free_mib() {
    awk '/^MemFree:/{print int($2/1024)}' /proc/meminfo
}

inside_vm() {
    command -v wsl.exe >/dev/null 2>&1 || return 0
    (cd "${TEMP:-/tmp}" && wsl.exe -d docker-desktop -u root -- sh -c "$1") >/dev/null 2>&1 || true
}

build_processes() {
    case "$1" in
        caligo-ml) echo "pip install" ;;
        caligo-client|caligo-share) echo "npm ci|npm run build|vite build|tsc -b" ;;
        caligo-api|caligo-analytics|caligo-notifications) echo "dotnet restore|dotnet publish|MSBuild.dll|VBCSCompiler" ;;
        *) echo "" ;;
    esac
}

for service in "${services[@]}"; do
    log="$(mktemp)"
    docker compose -f "$(windows "$here/$file")" --profile liquidsoap build --progress=plain "$service" > "$log" 2>&1 &
    build=$!
    lowest=999999
    stopped=0
    while kill -0 "$build" 2>/dev/null; do
        free="$(free_mib)"
        [ "$free" -lt "$lowest" ] && lowest="$free"
        if [ "$free" -lt "$floor" ] && ! grep -q "unpacking to\|naming to" "$log"; then
            kill "$build" 2>/dev/null || true
            pattern="$(build_processes "$service")"
            [ -n "$pattern" ] && inside_vm "pkill -f '$pattern'"
            stopped=1
            break
        fi
        sleep 2
    done
    status=0
    wait "$build" || status=$?
    inside_vm "echo 3 > /proc/sys/vm/drop_caches; echo 1 > /proc/sys/vm/compact_memory"
    if [ "$stopped" = 1 ]; then
        echo "$service: stopped at $lowest MiB free (floor $floor MiB), log in $log"
        exit 1
    fi
    if [ "$status" != 0 ]; then
        tail -20 "$log"
        echo "$service: build failed, log in $log"
        exit "$status"
    fi
    echo "$service: built, lowest free memory $lowest MiB"
    rm -f "$log"
    sleep 15
done
