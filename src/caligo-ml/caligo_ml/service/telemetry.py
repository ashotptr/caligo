import os

from opentelemetry import metrics

meter = metrics.get_meter("caligo_ml")
predictions = meter.create_counter("caligo_ml.predictions", description="Predictions made, by task and source")
rejected = meter.create_counter("caligo_ml.rejected_inputs", description="Requests refused by input validation")
job_runs = meter.create_counter("caligo_ml.job_runs", description="Batch job runs, by job and outcome")
providers = []


def instrument(app, span_exporter=None, metric_reader=None):
    if not os.getenv("OTEL_EXPORTER_OTLP_ENDPOINT") and span_exporter is None:
        return None
    from opentelemetry import trace
    from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor
    from opentelemetry.sdk.metrics import MeterProvider
    from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
    from opentelemetry.sdk.resources import Resource
    from opentelemetry.sdk.trace import TracerProvider
    from opentelemetry.sdk.trace.export import BatchSpanProcessor, SimpleSpanProcessor

    if span_exporter is None:
        from opentelemetry.exporter.otlp.proto.http.metric_exporter import OTLPMetricExporter
        from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter

        processor = BatchSpanProcessor(OTLPSpanExporter())
        metric_reader = PeriodicExportingMetricReader(OTLPMetricExporter())
    else:
        processor = SimpleSpanProcessor(span_exporter)
    resource = Resource.create({"service.name": os.getenv("OTEL_SERVICE_NAME", "caligo-ml")})
    tracer_provider = TracerProvider(resource=resource)
    tracer_provider.add_span_processor(processor)
    meter_provider = MeterProvider(resource=resource, metric_readers=[metric_reader] if metric_reader else [])
    if span_exporter is None:
        trace.set_tracer_provider(tracer_provider)
        metrics.set_meter_provider(meter_provider)
    FastAPIInstrumentor.instrument_app(app, tracer_provider=tracer_provider, meter_provider=meter_provider,
                                       excluded_urls="/health,/ready")
    providers.extend([tracer_provider, meter_provider])
    return tracer_provider, meter_provider


def shutdown():
    while providers:
        providers.pop().shutdown()
