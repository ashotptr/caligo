FROM ubuntu:24.04 AS build

RUN apt-get update \
 && apt-get install --yes --no-install-recommends build-essential libjansson-dev libssl-dev \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /root/CaligoEngine

COPY src/caligo/ ./

RUN make all

FROM ubuntu:24.04 AS pyenv

ARG CALIGO_PYTHON=lean
ARG CALIGO_EXTRA_PIP="python-dotenv"

RUN apt-get update \
 && apt-get install --yes --no-install-recommends python3 python3-venv ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /root/CaligoEngine

COPY src/caligo/requirements.txt ./requirements.txt

RUN mkdir -p venv \
 && if [ "$CALIGO_PYTHON" = "full" ]; then \
        cp requirements.txt /tmp/requirements.txt; \
    elif [ "$CALIGO_PYTHON" = "lean" ]; then \
        grep -v -i -E '^(torch|triton|nvidia-[a-z0-9-]+|transformers|sentence-transformers|tokenizers|safetensors|scikit-learn|scipy|sympy|mpmath|networkx|numpy|joblib|threadpoolctl|huggingface-hub|hf-xet|fsspec|filelock)==' \
            requirements.txt > /tmp/requirements.txt; \
    elif [ "$CALIGO_PYTHON" != "none" ]; then \
        echo "CALIGO_PYTHON must be full, lean or none" >&2; exit 1; \
    fi \
 && if [ "$CALIGO_PYTHON" != "none" ]; then \
        rmdir venv \
     && python3 -m venv venv \
     && venv/bin/pip install --no-cache-dir -r /tmp/requirements.txt $CALIGO_EXTRA_PIP; \
    fi

ARG CALIGO_YTDLP="yt-dlp[default,deno]==2026.8.19"

RUN if [ "$CALIGO_PYTHON" != "none" ]; then \
        venv/bin/pip install --no-cache-dir "$CALIGO_YTDLP" \
     && venv/bin/pip check; \
    fi

FROM ubuntu:24.04 AS runtime

RUN apt-get update \
 && apt-get install --yes --no-install-recommends libjansson4 libssl3t64 ca-certificates ffmpeg python3 tini \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10001 --home-dir /caligo-state/home --shell /usr/sbin/nologin caligo \
 && mkdir -p /caligo-state/home /caligo-copy \
 && chown -R caligo:caligo /caligo-state \
 && chmod 755 /root

COPY --from=build /root/CaligoEngine /root/CaligoEngine
COPY --from=pyenv /root/CaligoEngine/venv /root/CaligoEngine/venv
COPY --chmod=755 deploy/caligo/entrypoint.sh /usr/local/bin/caligo-entrypoint
COPY --chmod=755 deploy/caligo/cgi/radio_rotation /root/CaligoEngine/cgi_bin/radio_rotation
COPY deploy/caligo/page/harden.py /tmp/harden.py

RUN python3 /tmp/harden.py /root/CaligoEngine/public_html/index.html \
 && rm /tmp/harden.py

RUN cd /root/CaligoEngine \
 && mkdir -p data user_memories public_html/music public_html/mixtape \
 && chown caligo:caligo data user_memories public_html/music public_html/mixtape \
 && for file in chat_rooms.txt cgi_debug.log agent_memory.json cookies.txt; do \
        rm -f "$file" && ln -s "/caligo-state/$file" "$file"; \
    done \
 && for file in .env cert.pem key.pem; do \
        rm -f "$file" && ln -s "/caligo-copy/$file" "$file"; \
    done

ENV HOME=/caligo-state/home \
    PYTHONDONTWRITEBYTECODE=1 \
    CALIGO_VERBOSE=0

USER caligo
WORKDIR /root/CaligoEngine

EXPOSE 8080 8082 9001 5222

HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && exec 4<>/dev/tcp/127.0.0.1/9001"]

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/caligo-entrypoint"]
