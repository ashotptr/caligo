FROM ubuntu:24.04

RUN apt-get update \
 && apt-get install --yes --no-install-recommends ffmpeg python3-minimal \
 && rm -rf /var/lib/apt/lists/*

COPY run.sh /usr/local/bin/lab-source
COPY signal.py /usr/local/bin/lab-signal

RUN chmod 0755 /usr/local/bin/lab-source /usr/local/bin/lab-signal

ENTRYPOINT ["/usr/local/bin/lab-source"]
