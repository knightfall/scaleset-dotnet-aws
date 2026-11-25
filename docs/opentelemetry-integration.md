# OpenTelemetry integration plan

Implementation specification for [T19 / #20](https://github.com/knightfall/scaleset-dotnet-aws/issues/20), with host, deployment, and qualification work in T20, T21, T25, and T26. This document expands the [T19 task](tasks/M5-cdk-packaging-matrix.md#t19-implement-opentelemetry-tracing-metrics-and-otlp-logging). The linked issues record completed delivery and verification. Current exporter settings and query recipes are in [telemetry operations](operator-telemetry.md).

## Outcomes and scope

An operator must be able to answer which profile is short of capacity, whether GitHub admission or AWS provisioning is delaying it, which effects remain uncertain, and whether ownership, recovery, or bootstrap is failing. One controller may supervise multiple EC2 profiles with different Launch Templates and multiple Fargate profiles with different task definitions or clusters. Each profile must remain distinguishable without creating a service or exporter per profile.

Deliver correlated traces and structured logs, bounded metrics, an offline diagnostic command, and documented dashboard/alert queries through an operator-supplied OTLP receiver. The controller must continue safe lifecycle work during telemetry outages. Observability is diagnostic evidence; it never authorizes admission, proves a job completed, or replaces durable lifecycle records.

V1 exports controller signals and instruments the reusable bootstrap library. Bootstrap process entry points retain sanitized console logging under T14/T17; controller-observed handoff/startup measurements must say what was observed. A separate runner-side OTLP pipeline, collector deployment, vendor-specific dashboards, automatic infrastructure discovery, and workflow/job log ingestion are outside this feature. Do not distribute controller exporter credentials to runner jobs.

## Signal ownership and released SDK contracts

| Component | Responsibility |
| --- | --- |
| `Core`, `DynamoDb`, `GitHub`, `Providers.Ec2`, `Providers.Fargate`, `Handoff`, `RunnerBootstrap` | Emit `ILogger<T>`, `ActivitySource`, and `Meter` signals. No exporter, Serilog, or OpenTelemetry SDK initialization; no AWS/GitHub SDK types added to Core. |
| `Controller` composition root | Own one trace provider, one metric provider, and one Serilog pipeline; build one validated settings/resource snapshot for all three signals. |
| Serilog pipeline | Export logs through `Serilog.Sinks.OpenTelemetry` and retain sanitized console output. No additional OpenTelemetry logging provider, SerilogTracing subscription, or log-to-span conversion. |
| OpenTelemetry SDK | Export activities and measurements through the OTLP exporter. Subscribe explicitly to the sources/meters below and approved HTTP/runtime instrumentation. |
| Operator receiver | Route, store, query, and optionally sample exported signals. Deployment and retention remain operator responsibilities. |

Use the central versions and locks already selected in `Directory.Packages.props`: OpenTelemetry hosting/exporter/HTTP/runtime `1.21.0`, Serilog hosting `11.0.0`, OTLP sink `4.2.0`, console sink `6.1.1`. Verify implementation APIs against these packages; a documentation example is not permission to upgrade dependencies or import `Knightfall.ScaleSet.Hosting`.

The released `Knightfall.ScaleSet.*` `0.1.0-alpha.1` contracts are:

| Subscription | Released behavior | AWS integration |
| --- | --- | --- |
| Activity source `ScaleSet.Client` | `scaleset.client.request` activities | Subscribe once; retain the SDK's logical request activity and its distinct HTTP transport child when enabled. |
| Meter `ScaleSet.Client` | `scaleset.client.requests`, `scaleset.client.retries`, `scaleset.client.request.duration` (seconds) | Subscribe once; preserve bounded `dependency`, `method`, `status_class`, and `retry_kind` attributes as appropriate to each instrument. |
| Activity source `ScaleSet.Listener` | Pump `scaleset.listener.step` and `scaleset.listener.operation` activities | Subscribe once; wrap AWS supervision/handler boundaries to provide profile context. Legacy listener poll/process activities are not the pump contract. |
| Listener metrics | No `ScaleSet.Listener` meter in this release | Emit AWS-owned listener measurements from application adapters; do not invent an upstream meter or require an SDK change. |
| `Knightfall.ScaleSet.Aws.*` | New application instrumentation | Maintain a tested, explicit source/meter inventory named by owning assembly; keep instrumentation lifetimes independent of individual profiles. |

Verify these contracts against the release [Client diagnostics](https://github.com/knightfall/scaleset.NET/blob/a0bc2060715767bff0f1a22a9f328689ed363493/src/ScaleSet.Client/Transport/ScaleSetClientDiagnostics.cs), [Listener pump](https://github.com/knightfall/scaleset.NET/blob/a0bc2060715767bff0f1a22a9f328689ed363493/src/ScaleSet.Listener/ScaleSetListenerPump.cs), and [Listener operations](https://github.com/knightfall/scaleset.NET/blob/a0bc2060715767bff0f1a22a9f328689ed363493/src/ScaleSet.Listener/ListenerOperations.cs). Pin captured-payload assertions to the consumed package version.

## Resources, profiles, and correlation

Use the same application identity on OTLP logs, traces, and metrics:

- `service.name`: default `scaleset-aws-controller`; allow a validated `OTEL_SERVICE_NAME` override.
- `service.namespace`: `knightfall.scaleset.aws`.
- `service.version`: the build's informational version, including commit provenance when available.
- `service.instance.id`: an opaque value generated once per process start; never reuse it across controller replacements.
- `deployment.environment.name`: explicit deployment tier, default `development` for local commands; deployed examples must specify their tier.
- `scaleset.deployment.name`: validated configuration `metadata.name`; `cloud.provider=aws`, account ID, and Region may come from verified configuration without metadata/network lookups. Omit unavailable cloud identity in smoke mode.

Use `deployment.environment.name`, as defined by the [deployment semantic convention](https://opentelemetry.io/docs/specs/semconv/resource/deployment-environment/), rather than the older `deployment.environment` spelling. Reserve application identity fields against arbitrary resource overrides. Accept only a documented allowlist of additional resource attributes; do not auto-export environment variables, labels, command lines, ECS task ARNs, or credential-bearing URLs.

Put validated `profile` and `provider` on AWS operation spans and structured log scopes, and on the relevant metric instruments. Profiles share a service identity. Do not use Launch Template IDs/versions, task definition revisions, runner groups, GitHub routing labels, or config digests as metric dimensions. A safe operation ID or AWS resource ID may appear in an explicitly approved log/span field for diagnosis, with length limits; it must never migrate into a metric label or resource attribute.

Start short activities around reconciliation, listener steps, persistence, and provider operations. Preserve natural parent/child relationships for awaited work; log records within an activity must carry its OTLP `TraceId`/`SpanId`. A new lease or restarted process starts new activities. Do not hold a span open for a session, runner lifetime, lease, or entire job, and do not persist tracing data in authoritative checkpoint/effect identities.

Use links only when a trustworthy, bounded prior context is actually available. SQS/EventBridge observations without trusted context start independent activities with safe correlation fields. V1 does not add trace baggage to JIT payloads, encrypted offers, queue URLs, or runner environments. A retry can be correlated by an operation ID without pretending that a durable crash/replay is a continuous trace. Trace IDs are correlation fields, never metric dimensions; metrics correlate through resources/profile/time and, where supported and verified, exemplars.

## Instrumentation inventory

Span names use a stable `scaleset.aws.` prefix and operation names, never resource identifiers. Each span records only approved bounded `operation`, `outcome`, and `reason` values plus safe diagnostic fields. Expected deferrals and shutdown cancellation are distinguished from failures; ambiguous remote outcomes remain `unknown`, not successful.

| Boundary | Required visibility |
| --- | --- |
| Listener supervision and handler adapters | Poll/step, delivery admission, acquisition, checkpoint persistence, acknowledgement and settlement retry, cooldown, exact-session retirement and quarantine. Preserve Client/Listener nesting. |
| Planner and reconciliation | Per-profile demand/capacity decision, configured limits, unavailable observations, retained uncertainty, retirement progress. No span per runner in an inventory scan. |
| Ownership and DynamoDB | Lease acquire/renew/loss, fenced rejection, transaction conflict, journal transitions, bounded active-effect rebuild pages and unresolved effects. No item bodies or expression values. |
| EC2 | Launch Template resolution/drift, launch attempt/readback, AZ failover, inventory, safe targeted termination. Logical operation spans distinguish retries from new launch intents. |
| Fargate | Task definition preflight, RunTask/replay/readback, inventory and StopTask. A STOPPED task is not labeled as proof of GitHub completion. |
| Handoff and bootstrap | Offer publish/consume/revoke, expiry and rejection, and startup observations with their evidence source. No JIT, key material, ciphertext, nonce, or signed metadata payload. |
| Event processing | SQS receive/process/delete, duplicate/out-of-order handling and event age; no raw EventBridge/SQS body or receipt handle. |

The [controller metric catalog](telemetry-metrics.md) gives the units and observed boundaries. The following names are required, all prefixed `scaleset.aws.`. Counts use explicit units such as `{runner}`, `{operation}`, or `{page}`. All durations use `s`.

| Instrument suffix | Kind | Meaning |
| --- | --- | --- |
| `capacity.assigned`, `.active`, `.idle`, `.starting`, `.unknown`, `.deficit`, `.drift`, `.orphan_candidates` | Observable gauges | Separate per-profile counts; never coerce missing evidence into zero. Document the precise existing planner meaning of each count. |
| `inventory.age`, `reconcile.age`, `cleanup.oldest_age` | Observable gauges | Age of the last valid observation, last completed reconciliation, and oldest eligible pending cleanup. Expose freshness alongside capacity. |
| `reconcile.duration`, `listener.operation.duration`, `provider.operation.duration`, `handoff.duration`, `startup.duration` | Histograms | Elapsed time at the named boundary, with bounded outcome/reason and documented start/end evidence. |
| `listener.operations`, `provider.operations`, `lease.losses`, `dynamodb.conflicts`, `effect.rebuild.pages`, `ec2.az_failovers`, `ec2.template_drifts` | Counters | Completed attempts/events, including bounded outcomes. Retries count as attempts; they do not inflate distinct launches. |
| `effects.unfinished`, `listener.cooldown.remaining`, `profile.ready` | Observable gauges | Current unresolved effects, seconds of cooldown, and authoritative per-profile readiness. |

Metric callbacks read immutable cached snapshots and perform no I/O or locks on lifecycle work. On lease loss, stop publishing authoritative capacity for that ownership and report readiness loss; after retirement, remove the profile's observations. Distinguish a valid zero from absent/stale data in exported measurements and queries. Client SDK metrics remain process-level unless explicitly enriched through a tested bounded view; AWS listener metrics provide profile breakdown without modifying the SDK.

For custom AWS metrics, allow only the bounded subset needed by each instrument from `profile`, `provider`, `operation`, `outcome`, and `reason`; environment/deployment identity belongs on the resource. Profile values come from validated configured profiles, not incoming events. Unknown reasons collapse to `other`. Never take exception text as a label.

Maintain separate allowlists for Client SDK and built-in HTTP/runtime instruments. In particular, retain the Client attributes listed above instead of applying the AWS tag list to every meter. Apply views or drop unwanted instruments to control external endpoint names, errors, and other dynamic dimensions. Forbid job/request/runner/instance/task/trace identifiers across all metric attributes. Bound metric streams with a recorded per-instrument limit (initial target: 2,000), test overflow behavior, and budget series as configured profiles × enabled providers × actual operation/outcome combinations. Follow the [.NET metric guidance](https://opentelemetry.io/docs/languages/dotnet/metrics/best-practices/) while verifying the pinned SDK's limit/view APIs.

## Configuration contract

Telemetry is process/deployment configuration, not per-profile routing configuration. Keep v1 runner YAML and digest-pinned runtime JSON unchanged. T20 exposes a typed, validated host settings model; T21 maps explicit CDK host deployment properties to that model's environment variables. No generic arbitrary environment-variable passthrough. Exporter changes require a host restart but do not change allocation identity or generation semantics.

| Setting | V1 behavior |
| --- | --- |
| No endpoint | Console logging only; do not silently dial localhost. `telemetry-smoke` requires an endpoint. |
| `OTEL_SDK_DISABLED=true` | Disable all remote signal exports, including the separately configured Serilog OTLP sink; keep sanitized console logs. Reject a contradictory explicit `telemetry-smoke` invocation. |
| `--otlp-endpoint` | Highest-priority common base endpoint for all signals. Reject simultaneous per-signal endpoint overrides to avoid an ambiguous CLI contract. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` and `..._TRACES_ENDPOINT`, `..._METRICS_ENDPOINT`, `..._LOGS_ENDPOINT` | Per-signal endpoint wins over common environment endpoint. With no common endpoint, only explicitly configured signals export; smoke requires all three. |
| Common/per-signal `..._PROTOCOL` | Support `grpc` and `http/protobuf`, with per-signal precedence. Application default is explicit `grpc`; reject unsupported values. Do not infer protocol from a port. |
| Common/per-signal `..._HEADERS` | Parse once into secret-bearing in-memory settings. A per-signal setting replaces the common header set. Require HTTPS on the effective endpoint for each remote signal that sends headers. Allow local HTTP loopback for smoke tests. Never print values or serialize them into config artifacts. |
| Common/per-signal `..._TIMEOUT` | Positive milliseconds, default 3,000 and maximum 5,000 for this controller. Per-signal precedence; all retries fit the same deadline. |
| `OTEL_TRACES_SAMPLER` / `OTEL_TRACES_SAMPLER_ARG` | Default `parentbased_traceidratio` with root ratio `0.1`; permit a validated ratio in `[0,1]`. Permit `parentbased_always_on` for diagnosis. Smoke/contract tests explicitly sample all test traces. |
| `OTEL_METRIC_EXPORT_INTERVAL` | Default 30,000 ms; validate a documented finite range compatible with the exporter deadline. Smoke forces collection without waiting for the production interval. |
| `OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES` | Apply the resource allowlist and reserved-field rules above; reject malformed or conflicting identity fields. |

Resolve all settings once before starting AWS/GitHub clients. Disable secondary environment parsing by the logging sink so it cannot override validated options; the [Serilog sink documents both this override behavior and log/trace mapping](https://github.com/serilog/serilog-sinks-opentelemetry). Explicitly configure trace/metric exporters and logs separately; an all-signal helper must not create a second log exporter.

For HTTP/protobuf, append `v1/traces`, `v1/metrics`, and `v1/logs` to a common base URL; use per-signal URLs exactly as supplied. Normalize once, then adapt to each pinned exporter/sink API without adding the suffix twice. Follow the [OTLP endpoint rules](https://opentelemetry.io/docs/specs/otel/protocol/exporter/). Validate absolute HTTP(S) URLs, reject user info/query/fragment and credential-bearing endpoints, and test trailing slashes and path prefixes. Permit HTTP for explicit loopback/private receivers; use HTTPS for remote examples with normal certificate verification, never a validation bypass.

ECS header secrets are explicit secret references injected by the **execution role**, never plaintext CDK context, task environment values, runtime JSON, synthesized outputs, or log messages. Any extra secret/KMS grants belong to the narrowly scoped execution role; the dry-run task role still has no Secrets Manager reads. Secret rotation takes effect on task replacement. V1 requires no custom collector trust/mTLS feature; do not conflate this standard OTLP transport with deferred GitHub transport changes.

Example target environment, to become executable with T19/T20 (not a command available in the current scaffold):

```sh
OTEL_SERVICE_NAME=scaleset-aws-controller
OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=development
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_TRACES_SAMPLER=parentbased_traceidratio
OTEL_TRACES_SAMPLER_ARG=0.1
OTEL_METRIC_EXPORT_INTERVAL=30000
```

## Redaction, exporter isolation, and shutdown

Use structured, allowlisted events at emission boundaries. Sanitize nested properties and exception representations before both console and OTLP sinks. Do not attach raw HTTP headers/bodies, authorization tokens, App private keys, PATs, exchanged credentials, JIT configuration, session queue URLs, exporter headers, or full exception `ToString()` values. Apply equivalent limits/redaction to span attributes, status descriptions and events. Avoid recording HTTP paths/query strings on sensitive SDK/queue/metadata requests; automatic instrumentation needs its own review and payload tests.

Keep HTTP transport and runtime instrumentation narrowly subscribed. Suppress HTTP tracing/logging for OTLP export calls, including the Serilog sink, to prevent recursive export loops. Do not auto-enable AWS SDK instrumentation on top of manual provider spans without proving request accounting and privacy. Health probes produce no routine request logs/spans; telemetry failure alone must not fail `/healthz`, revoke ownership, or mark a profile unready.

Batch export asynchronously with bounded queues (initial limits: 2,048 pending spans and 2,048 pending log events), bounded record sizes, and bounded retry time. Set attribute count/value and log record size limits in the implementation and record the tested values; never enqueue unbounded object graphs. Drop telemetry on saturation and emit a rate-limited, redacted local diagnostic. Error/warning logs remain eligible for export independently of trace sampling; a sampled-out trace may legitimately leave a correlated log without a stored span. Head sampling cannot promise that every failed operation has a trace.

Export must not run synchronously within an acknowledgement, DynamoDB transaction, lease renewal, or lifecycle critical section. Metrics callbacks never query AWS or DynamoDB. Export failures must not recursively generate exported failures. Test queue saturation, rejected requests, connection refusal, stalled response bodies, and recovery; record memory bounds and continued lifecycle progress, not merely completion of an exporter mock.

Flush and dispose all three pipelines under **one shared maximum of five seconds**, within T20's total 60-second SIGTERM budget and 90-second ECS stop timeout. Do not give each sink a fresh five-second budget or let implicit host disposal wait indefinitely after a timeout wrapper returns. Stop application work safely first, then use the remaining telemetry budget. Dropped final telemetry is acceptable; delayed ownership shutdown is not.

## Delivery slices and acceptance evidence

Keep [T19 / #20](https://github.com/knightfall/scaleset-dotnet-aws/issues/20) as the implementation owner; use focused PRs for the slices below. Do not close it after only configuring exporters. Completed T03/T04 work need not reopen because v1 telemetry remains host configuration.

| Slice / issue | Deliverable and evidence |
| --- | --- |
| T19 configuration and composition | One validated settings/resource model, explicit source/meter inventory, single export owner per signal, protocol/endpoint/header/disabled-mode tests, and library dependency-boundary checks. |
| T19 instrumentation and privacy | All boundary/instrument catalog entries, fresh/unknown/retired profile semantics, allowlisted logs/attributes, Client/Listener package contract fixtures, and cardinality/secret tests. |
| T19 failure isolation and smoke | Bounded batching/retry/flush, recovery/outage/saturation tests, recursive-export suppression, offline `telemetry-smoke`, and the captured-payload acceptance suite below. |
| [T20 / #21](https://github.com/knightfall/scaleset-dotnet-aws/issues/21) | Compose the pipeline for `run` and `reconcile`; diagnostic commands initialize only their needed dependencies. Verify health behavior and total process shutdown with a hung receiver. |
| [T21 / #22](https://github.com/knightfall/scaleset-dotnet-aws/issues/22) | CDK environment/secret-reference wiring, execution-role grant scoping, no secret values in templates, no extra runner permissions, and zero always-on collector sidecars across the host/backend/mode matrix. |
| [T25 / #26](https://github.com/knightfall/scaleset-dotnet-aws/issues/26) | Full mixed-profile emulation with OTLP capture, distinct profile attribution, takeover and retirement freshness, collector outage/recovery while acknowledgements/leases progress, and dry-run's zero-cloud-mutation audit. |
| [T26 / #27](https://github.com/knightfall/scaleset-dotnet-aws/issues/27) | Operator configuration examples, metric catalog and dashboard/alert query recipes, receiver/network/auth troubleshooting, privacy/cardinality budgets, and qualification limits. |

The acceptance suite must decode captured OTLP payloads, not just assert registration calls:

1. Export one controlled smoke log, span and metric over gRPC and HTTP/protobuf. Assert resource parity, proper log trace/span IDs, explicit units, source/meter names, paths, and healthy-path single pipeline export. OTLP retry can redeliver a batch; do not claim end-to-end exactly-once ingestion.
2. Drive actual pinned Client/Listener packages using fake GitHub HTTP and AWS adapters. Assert the pump/operation/client relationships and AWS profile scope, without inventing a Listener meter. Keep functional metric assertions independent of trace sampling.
3. Run at least two EC2 profiles with distinct Launch Templates and two Fargate profiles with distinct task definitions/clusters and disjoint labels. Vary thousands of synthetic runner/job/resource IDs while holding configured profiles fixed; metric series must remain within the declared budget. Assert no forbidden labels, including in SDK/HTTP/runtime metrics or resources.
4. Inject synthetic secrets into exception text, nested properties, HTTP failures, URLs, headers and JIT handoff errors. Decode all signals and inspect console/self-diagnostic output for leakage. Do not publish captured sensitive fixtures as CI artifacts.
5. Exercise disabled/console-only, malformed options, partial signal configuration, environment/CLI precedence, unavailable receiver, authentication rejection, saturation, recovery, and shutdown. Invalid configuration fails before cloud work; a valid but unavailable receiver does not prevent control-loop progress. Instrumented and disabled runs must have identical lifecycle decisions.
6. `telemetry-smoke` uses no AWS/GitHub client or credential provider. It reports success only after successful export responses for its signals within the overall deadline; errors/partial rejection/timeout produce a nonzero exit. This proves receiver acceptance, not durable storage or backend queryability. Contract tests separately prove payload content/correlation.

T19 records commands, package versions, payload assertions, queue/cardinality limits, and outage/shutdown evidence in its issue. T25 adds local emulation results. No live cloud deployment or credentialed qualification is required to implement this plan.

## Operational views and release gate

T26 must supply backend-neutral queries and example thresholds for:

- **Profile capacity:** assigned/active/idle/starting/unknown/deficit, freshness, configured limits, and readiness grouped by deployment/profile/provider. Treat missing series as missing data, not zero demand.
- **GitHub supervision:** poll/ack latency, admission outcomes, cooldown, quarantined/uncertain recovery, and Client request/retry rates.
- **AWS execution:** launch/cleanup outcomes and latency, EC2 capacity failover/template drift, Fargate uncertainty/replay cutoff, and handoff/startup failures.
- **Durability and ownership:** lease losses, fenced operations, conflicts, oldest eligible cleanup, effect rebuild progress, and unresolved effects.
- **Runtime and export:** process/runtime health, receiver/export failures, dropped telemetry diagnostics, and absent expected data. Receiver-side alerts must detect silence when the exporter itself cannot report.

Choose sustained alert windows relative to the configured poll/reconciliation/lease budgets; avoid alerts on a single empty poll or expected retry. Do not average away an unhealthy profile or count inactive owners' stale capacity as current. Include a troubleshooting path from a profile alert to a safe operation log and available trace, with a fallback when sampling omitted the trace.

The feature is ready when T19's contract/failure tests, T20/T21 host wiring, T25 local mixed-profile verification, and T26 operator guidance are merged with evidence. A successful loopback export does not qualify production receiver authorization, TLS/network paths, retention, alert delivery, live AWS behavior, or runner OS isolation; record those target checks separately before deployment.
