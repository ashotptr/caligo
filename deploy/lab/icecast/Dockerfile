FROM debian:bookworm-slim

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends icecast2 \
 && rm -rf /var/lib/apt/lists/*

COPY icecast.xml /etc/lab/icecast.xml
COPY entrypoint.sh /usr/local/bin/lab-icecast

RUN chmod 0755 /usr/local/bin/lab-icecast

USER icecast2

EXPOSE 8000

ENTRYPOINT ["/usr/local/bin/lab-icecast"]
