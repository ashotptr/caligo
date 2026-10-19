#!/bin/sh
set -eu

case "${LAB_SOURCE_PASSWORD:?set LAB_SOURCE_PASSWORD}" in
    *[!A-Za-z0-9._-]*)
        echo "lab-icecast: LAB_SOURCE_PASSWORD may only use letters, digits, dot, dash and underscore" >&2
        exit 1
        ;;
esac

sed "s/@PASSWORD@/$LAB_SOURCE_PASSWORD/g" /etc/lab/icecast.xml > /tmp/icecast.xml

exec icecast2 -c /tmp/icecast.xml
