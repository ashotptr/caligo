FROM ubuntu:24.04 AS build

RUN apt-get update \
 && apt-get install --yes --no-install-recommends build-essential python3 libssl-dev libjansson-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /streaming

COPY Makefile ./
COPY src/ src/
COPY tests/ tests/

RUN make EXTRA=-Werror all unit

FROM build AS quic-build

ARG PICOQUIC_COMMIT=cc11becf4e90667e8145b4c7dd73985b17c6ddf8

RUN apt-get update \
 && apt-get install --yes --no-install-recommends ca-certificates cmake git pkg-config \
 && rm -rf /var/lib/apt/lists/* \
 && git init --quiet /opt/picoquic \
 && git -C /opt/picoquic fetch --quiet --depth 1 https://github.com/private-octopus/picoquic.git ${PICOQUIC_COMMIT} \
 && git -C /opt/picoquic checkout --quiet FETCH_HEAD \
 && cmake -S /opt/picoquic -B /opt/picoquic/build -DCMAKE_BUILD_TYPE=Release -DPICOQUIC_FETCH_PTLS=ON \
      -DBUILD_DEMO=OFF -DBUILD_PQBENCH=OFF -DBUILD_LOGLIB=OFF -DBUILD_LOGREADER=OFF -DBUILD_TESTING=OFF \
 && cmake --build /opt/picoquic/build --parallel --target picoquic-core picohttp-core

RUN make EXTRA=-Werror relay-quic

FROM ubuntu:24.04 AS vod

RUN apt-get update \
 && apt-get install --yes --no-install-recommends libssl3t64 libjansson4 \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10003 --no-create-home --shell /usr/sbin/nologin vod \
 && mkdir -p /media/library \
 && chown 10004:10004 /media/library

COPY --from=build /streaming/build/caligo-vod /usr/local/bin/caligo-vod

USER vod

EXPOSE 8010

HEALTHCHECK --interval=15s --timeout=5s --start-period=5s --retries=4 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8010 && printf 'GET /healthz HTTP/1.0\\r\\n\\r\\n' >&3 && grep -q '200 OK' <&3"]

ENTRYPOINT ["/usr/local/bin/caligo-vod"]

FROM ubuntu:24.04 AS packager

RUN apt-get update \
 && apt-get install --yes --no-install-recommends ffmpeg libssl3t64 libjansson4 \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10004 --no-create-home --shell /usr/sbin/nologin packager \
 && mkdir -p /media/library /media/incoming \
 && chown 10004:10004 /media/library

COPY --from=build /streaming/build/caligo-packager /usr/local/bin/caligo-packager

USER packager

ENV PACKAGER_HEARTBEAT=/tmp/packager.alive

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["bash", "-c", "test -n \"$(find /tmp/packager.alive -mmin -2 2>/dev/null)\""]

ENTRYPOINT ["/usr/local/bin/caligo-packager"]

FROM ubuntu:24.04 AS relay

RUN useradd --system --uid 10002 --no-create-home --shell /usr/sbin/nologin relay

COPY --from=build /streaming/build/caligo-relay /usr/local/bin/caligo-relay

USER relay

EXPOSE 8000

HEALTHCHECK --interval=15s --timeout=5s --start-period=5s --retries=4 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8000 && printf 'GET /healthz HTTP/1.0\\r\\n\\r\\n' >&3 && grep -q '200 OK' <&3"]

ENTRYPOINT ["/usr/local/bin/caligo-relay"]

FROM ubuntu:24.04 AS netem

RUN apt-get update \
 && apt-get install --yes --no-install-recommends iproute2 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /streaming/build/caligo-netem /usr/local/bin/caligo-netem

ENTRYPOINT ["/usr/local/bin/caligo-netem"]

FROM ubuntu:24.04 AS relay-quic

RUN apt-get update \
 && apt-get install --yes --no-install-recommends libssl3t64 \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10002 --no-create-home --shell /usr/sbin/nologin relay

COPY --from=quic-build /streaming/build/caligo-relay-quic /usr/local/bin/caligo-relay-quic

USER relay

EXPOSE 8000 8000/udp

HEALTHCHECK --interval=15s --timeout=5s --start-period=5s --retries=4 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8000 && printf 'GET /healthz HTTP/1.0\\r\\n\\r\\n' >&3 && grep -q '200 OK' <&3"]

ENTRYPOINT ["/usr/local/bin/caligo-relay-quic"]
