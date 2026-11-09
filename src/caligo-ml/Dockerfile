FROM python:3.13-slim
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY requirements-train.txt ./
RUN pip install --no-cache-dir -r requirements-train.txt
COPY requirements-serve.txt ./
RUN pip install --no-cache-dir -r requirements-serve.txt
COPY caligo_ml/ caligo_ml/
RUN useradd --create-home --uid 10001 app
USER app
ENV PYTHONUNBUFFERED=1 NUMBA_CACHE_DIR=/tmp/numba PREFECT_HOME=/tmp/prefect
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health', timeout=4)"
CMD ["uvicorn", "caligo_ml.service.app:app", "--host", "0.0.0.0", "--port", "8080", "--no-access-log"]
