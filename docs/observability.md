# Observability

GenOnlineService sends logs, metrics, and traces with OpenTelemetry.

Send OTLP to a collector such as Grafana Alloy. The collector can route metrics to Prometheus, logs to Loki, and traces to Tempo. Sentry is optional and receives errors, and optionally logs and traces. Metrics only go over OTLP.

## Configuration

Remote export is off by default:

```json
{
  "OpenTelemetry": {
    "Enabled": false,
    "OtlpEndpoint": "http://localhost:4317",
    "ServiceName": "GenOnlineService",
    "TraceSampleRatio": 1.0,
    "SlowDatabaseCommandMilliseconds": 500
  },
  "Sentry": {
    "enabled": false,
    "dsn": "",
    "environment": "production",
    "enable_logs": false,
    "traces_sample_rate": 0.1
  }
}
```

Production values can use environment variables:

```text
OpenTelemetry__Enabled=true
OpenTelemetry__OtlpEndpoint=http://alloy:4317
OpenTelemetry__TraceSampleRatio=0.25
Sentry__enabled=true
Sentry__dsn=https://...
Sentry__environment=production
```

`TraceSampleRatio` controls how many traces OpenTelemetry keeps. Sentry can sample those traces again with `traces_sample_rate`. Sentry keeps 10% of those traces by default (`0.1`). Raise it while validating the setup, then lower it if volume or cost is a concern.

## Sentry

- Errors: `ILogger` entries at Error or above become Sentry events, Warning and above become breadcrumbs. This works without OpenTelemetry.
- Logs: `enable_logs` sends `ILogger` entries to Sentry Logs. Off by default.
- Traces: only when `OpenTelemetry:Enabled` is also true, because Sentry receives the OpenTelemetry spans. `traces_sample_rate` applies to those.
- Metrics: not sent. Sentry 6.8 cannot read .NET meters, so metrics go to the OTLP collector only.

Sentry's own EF Core/SqlClient listener is disabled so SQL never ends up in Sentry span descriptions.

## What is collected

- Logs from `ILogger`, with trace IDs when available.
- HTTP requests and outgoing HTTP calls.
- Database commands and connection-pool health, for both MySQL/MariaDB (MySqlConnector) and PostgreSQL (Npgsql).
- WebSocket message spans, connections, and failures.
- Authentication, matchmaking, external publication, background jobs, and security actions.
- .NET runtime health.

Fast tick loops are not traced on every pass. That would create too much data.

## Metrics

Database, HTTP, and runtime metrics come from the built-in MySqlConnector, Npgsql, ASP.NET Core, HttpClient, and runtime meters. Application metrics start with `genonline.`:

| Metric | Type | Labels |
| --- | --- | --- |
| `genonline.players.online` | gauge | |
| `genonline.lobbies.active` | gauge | |
| `genonline.authentication.attempts` | counter | `flow`, `source`, `outcome` |
| `genonline.websocket.connections.active` | gauge | `session.type` |
| `genonline.websocket.failures` | counter | `direction`, `reason` |
| `genonline.websocket.send_queue.depth` | gauge | |
| `genonline.matchmaking.queue.players` | gauge | `playlist` |
| `genonline.matchmaking.wait.duration` | histogram (s) | `playlist`, `outcome` |
| `genonline.matchmaking.matches` | counter | `playlist`, `outcome` |
| `genonline.background_job.failures` | counter | `job` |
| `genonline.background_job.last_success.time` | gauge (unix s) | `job` |
| `genonline.external_publication.failures` | counter | `outcome` |
| `genonline.external_publication.backlog.age` | gauge (s) | |
| `genonline.security.actions` | counter | `action`, `outcome` |

`genonline.security.actions` covers HTTP and chat rate-limit rejections, moderation actions, and anti-cheat reviews and bans.

Metric labels use small fixed sets. They do not contain player, lobby, or match IDs.

## Database data

Spans never carry SQL text, connection strings, database users, or the Npgsql data source name (which is derived from the connection string): a span processor removes them before Sentry or the exporter sees the span.

Slow-command logs contain only the operation, duration, and result. They never contain SQL or parameters.

EF Core's command events (executing, executed, error, canceled) are ignored in the context options, so EF never logs SQL text whatever the logging configuration says. The `MySqlConnector` and `Npgsql` log categories default to Warning.

## Outbound URLs

The TURN key is part of the Cloudflare API path, and configured URLs may carry tokens. Outbound HTTP spans therefore keep only the scheme, host, and port of the URL. Incoming request spans keep their path, and the ASP.NET Core instrumentation redacts query string values.

## Identifiers in logs and traces

Metrics never contain player, lobby, or match IDs. Logs and traces do include user, lobby, and match IDs (pseudonymous) so incidents can be traced. Display names appear only in Debug logs. Treat the collector and Sentry as systems that hold player identifiers.

Per-message WebSocket failures are logged at Error at most once per user per minute; the rest are Debug. Metrics and spans still record every failure.

## Alloy example

This receives all three OTLP signals and prints them for testing:

```alloy
otelcol.receiver.otlp "genonline" {
  grpc {
    endpoint = "0.0.0.0:4317"
  }

  output {
    metrics = [otelcol.processor.batch.genonline.input]
    logs    = [otelcol.processor.batch.genonline.input]
    traces  = [otelcol.processor.batch.genonline.input]
  }
}

otelcol.processor.batch "genonline" {
  output {
    metrics = [otelcol.exporter.debug.genonline.input]
    logs    = [otelcol.exporter.debug.genonline.input]
    traces  = [otelcol.exporter.debug.genonline.input]
  }
}

otelcol.exporter.debug "genonline" {}
```

Replace the debug exporter with the real Prometheus, Loki, and Tempo outputs.

## Useful alerts

- HTTP errors and slow requests.
- Database errors, slow commands, and pool pressure.
- Background job failures, or a `last_success.time` that stops moving.
- External publication failures and a growing backlog age.
- Long matchmaking waits and setup failures.
- WebSocket failures and a growing send queue.
- Spikes in rate-limit rejections or security actions.
- New Sentry error groups.
