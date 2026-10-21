#!/usr/bin/env bash
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose_dir="$(cd "$here/../compose" && pwd)"
host_floor="${LAB_HOST_FLOOR_MIB:-900}"
services=(caligo relay icecast mediamtx lab-web lab-source)

compose() {
    docker compose -f "$compose_dir/docker-compose.yml" -f "$compose_dir/docker-compose.lab.yml" \
        -f "$compose_dir/docker-compose.listen.yml" --profile lab "$@"
}

host_available_mib() {
    awk '/^MemAvailable:/ { available = $2 } /^MemFree:/ { free = $2 }
        END { if (available + free > 0) printf "%d\n", (available ? available : free) / 1024 }' /proc/meminfo 2>/dev/null || true
}

case "${1:-up}" in
    up)
        host="$(host_available_mib)"

        if [ -n "$host" ] && [ "$host" -lt "$host_floor" ]; then
            echo "listen: this machine is down to $host MiB free, under the $host_floor MiB this needs" >&2
            exit 1
        fi

        compose up -d --build "${services[@]}"
        compose up -d --build --no-deps caligo-client
        echo "listen: open http://localhost:8080/listen"
        ;;
    down)
        compose stop caligo-client "${services[@]}"
        compose rm -f caligo-client "${services[@]}"
        ;;
    *)
        echo "usage: $0 [up|down]" >&2
        exit 2
        ;;
esac
