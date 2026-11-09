from fastapi import FastAPI
from fastapi.testclient import TestClient
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

from caligo_ml.service import telemetry


def test_telemetry_stays_off_without_an_endpoint(monkeypatch):
    monkeypatch.delenv("OTEL_EXPORTER_OTLP_ENDPOINT", raising=False)

    assert telemetry.instrument(FastAPI()) is None
    assert telemetry.providers == []


def test_requests_are_traced_but_health_checks_are_not():
    traced = FastAPI()

    @traced.get("/songs")
    def listed():
        return []

    @traced.get("/health")
    def health():
        return {"status": "ok"}

    exporter = InMemorySpanExporter()
    telemetry.instrument(traced, span_exporter=exporter)
    with TestClient(traced) as client:
        client.get("/songs")
        client.get("/health")
    names = {span.name for span in exporter.get_finished_spans()}
    telemetry.shutdown()

    assert "GET /songs" in names
    assert not any("/health" in name for name in names)
