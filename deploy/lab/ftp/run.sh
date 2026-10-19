#!/bin/sh
child=""

trap 'kill "$child" 2>/dev/null; exit 0' TERM INT

while :; do
    vsftpd /etc/vsftpd.conf &
    child=$!
    wait "$child"
    echo "lab-ftp: vsftpd exited with $?, starting it again" >&2
    sleep 0.2
done
