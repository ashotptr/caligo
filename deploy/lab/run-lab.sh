#!/usr/bin/env bash
set -euo pipefail

scenario="${1:-baseline}"

if [ $# -gt 0 ]; then
    shift
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose_dir="$(cd "$here/../compose" && pwd)"
clickhouse="${LAB_CLICKHOUSE_URL:-http://127.0.0.1:8123}"
clickhouse_user="${LAB_CLICKHOUSE_USER:-blog}"
clickhouse_password="${LAB_CLICKHOUSE_PASSWORD:-blogpass}"
clickhouse_database="${LAB_CLICKHOUSE_DATABASE:-blog_analytics}"
site="${LAB_SITE_URL:-http://localhost:8080}"
floor="${LAB_MEMORY_FLOOR_MIB:-700}"
host_floor="${LAB_HOST_FLOOR_MIB:-500}"
contestants=(caligo relay icecast mediamtx lab-web lab-ftp)
sidecars=(netem-caligo netem-relay netem-icecast netem-mediamtx netem-web netem-ftp)
containers=(blog-caligo-1 blog-relay-1 blog-icecast-1 blog-mediamtx-1 blog-lab-web-1 blog-lab-ftp-1)
results=(postgres redis blog-analytics blog-api caligo-client)
built=(blog-api blog-analytics caligo-client)
scratch="$(mktemp -d)"
sampler=""
guardian=""

trap 'kill $sampler $guardian 2>/dev/null || true; rm -rf "$scratch"' EXIT

compose() {
    docker compose -f "$compose_dir/docker-compose.yml" -f "$compose_dir/docker-compose.lab.yml" \
        --profile lab --profile lab-run "$@"
}

start() {
    if ! compose up -d "$@"; then
        echo "lab: a dependency was slow to report healthy, trying once more"
        sleep 10
        compose up -d "$@"
    fi
}

new_id() {
    if [ -r /proc/sys/kernel/random/uuid ]; then
        cat /proc/sys/kernel/random/uuid
    elif command -v uuidgen >/dev/null 2>&1; then
        uuidgen | tr '[:upper:]' '[:lower:]'
    else
        python3 -c 'import uuid; print(uuid.uuid4())' 2>/dev/null || python -c 'import uuid; print(uuid.uuid4())'
    fi
}

clickhouse_query() {
    curl -fsS -u "$clickhouse_user:$clickhouse_password" --data-binary @- "$clickhouse/?database=$clickhouse_database&query=$1"
}

wait_for() {
    local what="$1"
    shift

    for _ in $(seq 1 120); do
        if "$@" >/dev/null 2>&1; then
            return 0
        fi

        sleep 2
    done

    echo "lab: $what did not come up" >&2

    return 1
}

healthy() {
    [ "$(docker inspect -f '{{.State.Health.Status}}' "$1" 2>/dev/null)" = "healthy" ]
}

vm_available_mib() {
    { docker exec blog-clickhouse-1 cat /proc/meminfo 2>/dev/null || true; } \
        | awk '/^MemAvailable:/ { printf "%d\n", $2 / 1024 }'
}

host_available_mib() {
    awk '/^MemAvailable:/ { available = $2 } /^MemFree:/ { free = $2 }
        END { if (available + free > 0) printf "%d\n", (available ? available : free) / 1024 }' /proc/meminfo 2>/dev/null || true
}

shortage() {
    local vm host

    vm="$(vm_available_mib)"
    host="$(host_available_mib)"

    if [ -n "$vm" ] && [ "$vm" -lt $(( floor + $1 )) ]; then
        echo "Docker's VM has $vm MiB free and this needs $(( floor + $1 )) MiB"
    elif [ -n "$host" ] && [ "$host" -lt "$host_floor" ]; then
        echo "this machine is down to $host MiB free, under its $host_floor MiB floor"
    fi
}

room() {
    local short

    short="$(shortage "$1")"

    if [ -n "$short" ]; then
        echo "lab: not starting $2: $short" >&2

        return 1
    fi
}

sample() {
    while :; do
        now="$(date -u '+%Y-%m-%d %H:%M:%S.%3N')"

        docker stats --no-stream --format '{{.Name}},{{.CPUPerc}},{{.MemUsage}}' "${containers[@]}" 2>/dev/null \
            | awk -F, -v run="$run_id" -v now="$now" '{
                cpu = $2; gsub("%", "", cpu);
                split($3, parts, " / "); memory = parts[1];
                value = memory; unit = memory;
                gsub(/[A-Za-z]+$/, "", value); gsub(/^[0-9.]+/, "", unit);
                factor = 1;
                if (unit == "KiB") factor = 1024; else if (unit == "MiB") factor = 1048576; else if (unit == "GiB") factor = 1073741824;
                else if (unit == "kB") factor = 1000; else if (unit == "MB") factor = 1000000; else if (unit == "GB") factor = 1000000000;
                printf "%s,%s,%s,%s,%d\n", run, $1, now, cpu, value * factor;
            }' \
            | clickhouse_query "INSERT%20INTO%20lab_resources%20FORMAT%20CSV" >/dev/null 2>&1 || true

        sleep 1
    done
}

guard() {
    local short

    while :; do
        short="$(shortage 0)"

        if [ -n "$short" ]; then
            echo "lab: stopping the run: $short" >&2
            : > "$scratch/starved"
            docker ps -q --filter label=com.docker.compose.project=blog --filter label=com.docker.compose.service=stream-lab \
                | xargs -r docker stop >/dev/null 2>&1 || true
            compose stop lab-source "${sidecars[@]}" "${contestants[@]}" >/dev/null 2>&1 || true

            return 0
        fi

        sleep 5
    done
}

run_id="$(new_id)"

echo "lab: building the images first, one at a time"
for service in "${contestants[@]}" lab-source netem-relay stream-lab "${built[@]}"; do
    compose build "$service"
done

echo "lab: starting ClickHouse"
start clickhouse
wait_for "ClickHouse" curl -fsS "$clickhouse/ping"

if ! room 1200 "the contestants and the load generator"; then
    exit 1
fi

echo "lab: starting the contestants, the source and the impairment sidecars"
start "${contestants[@]}" lab-source "${sidecars[@]}"
wait_for "the relay" healthy blog-relay-1

compose run --rm -T stream-lab schema

sample &
sampler=$!
guard &
guardian=$!

set +e
compose run --rm -T stream-lab "$scenario" --run-id "$run_id" "$@"
status=$?
set -e

kill $sampler $guardian 2>/dev/null || true
sampler=""
guardian=""

echo "lab: stopping the contestants to make room for the results stack"
compose stop lab-source "${sidecars[@]}" "${contestants[@]}"

if [ -e "$scratch/starved" ]; then
    echo "lab: run $run_id was cut short because the Docker host ran low on memory" >&2
    exit 1
fi

if ! room 700 "the results page and its database"; then
    echo "lab: run $run_id is stored in ClickHouse; start ${results[*]} with --no-deps once there is room" >&2
    exit 1
fi

echo "lab: starting the results page without Kafka and Mongo, which it does not need"
start --no-deps "${results[@]}"

echo
echo "lab: run $run_id finished with status $status"
echo "lab: results at $site/lab/$run_id (sign in as an admin)"

exit "$status"
