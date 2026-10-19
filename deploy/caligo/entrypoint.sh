#!/bin/sh
set -eu

ROOT=/root/CaligoEngine
COPY=/caligo-copy
STATE=/caligo-state

cd "$ROOT"

mkdir -p "$STATE/home" /tmp/caligo_downloads

for file in chat_rooms.txt cgi_debug.log agent_memory.json cookies.txt; do
    if [ ! -e "$STATE/$file" ]; then
        if [ -f "$COPY/$file" ]; then
            cp "$COPY/$file" "$STATE/$file"
        else
            : > "$STATE/$file"
        fi
    fi
done

PIDS=""

keep_running() {
    name="$1"
    shift

    (
        child=""

        trap 'if [ -n "$child" ]; then kill "$child" 2>/dev/null || true; fi; exit 0' TERM INT

        while true; do
            if [ "${CALIGO_VERBOSE:-0}" = "1" ]; then
                stdbuf -oL "$@" &
            else
                "$@" > /dev/null &
            fi

            child=$!

            wait "$child" || true

            echo "[caligo] $name stopped, starting it again in 2 seconds" >&2

            sleep 2
        done
    ) &

    PIDS="$PIDS $!"
}

trap 'kill $PIDS 2>/dev/null || true; exit 0' TERM INT

keep_running xmppd ./xmppd
keep_running bridge ./bridge
keep_running radio_server ./radio_server

if [ -r cert.pem ] && [ -r key.pem ]; then
    keep_running server_https ./server_https
else
    echo "[caligo] cert.pem or key.pem is missing, server_https is not started" >&2
fi

keep_running server_http ./server_http

echo "[caligo] started: xmppd 5222, bridge 8082, radio_server 9001, server_http 8080" >&2

wait
