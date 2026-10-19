FROM debian:bookworm-slim

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install --yes --no-install-recommends vsftpd \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /var/run/vsftpd/empty

COPY vsftpd.conf /etc/vsftpd.conf
COPY --chmod=0755 run.sh /usr/local/bin/lab-ftp

EXPOSE 21 21100-21199

ENTRYPOINT ["/usr/local/bin/lab-ftp"]
