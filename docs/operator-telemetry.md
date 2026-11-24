# Controller telemetry operations

Use the [operator runbook](operator-runbook.md) to deploy or replace the
controller and the [metric catalog](telemetry-metrics.md) for instrument units
and evidence. The controller sends traces, metrics, and sanitized structured
logs to an operator-managed OTLP receiver. Use the signals to locate a degraded
profile and then consult the controller's durable lifecycle records and exact
provider observations before taking action.

## Configure export

The controller reads telemetry settings at startup. Set them through process
environment variables for a local process, or use the CDK deployment's separate
`--telemetry` JSON file for ECS. Runner YAML and digest-pinned runtime JSON carry
the profile configuration. Telemetry settings apply to the whole controller.
Each profile is identified on its relevant signals.

| Setting | Accepted configuration |
| --- | --- |
| `OTEL_SDK_DISABLED` | `true` disables remote export of all signals and leaves sanitized console logging enabled. Supplied settings are still validated. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Common base URL. `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`, `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`, and `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` take precedence for their respective signals. With only a signal endpoint, only that signal exports. With no endpoints, the controller uses console logging. |
| `--otlp-endpoint URL` | Common base URL override on `run` and `telemetry-smoke`. Clear all three per-signal endpoint variables before using this option. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` by default, or `http/protobuf`. A per-signal `_PROTOCOL` replaces the common setting. |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | Integer milliseconds, 1–5,000, default 3,000. A per-signal `_TIMEOUT` replaces the common setting. |
| `OTEL_EXPORTER_OTLP_HEADERS` | Comma-separated key/value pairs, parsed once. Percent-encode delimiters in values, for example `%20` for a space. A per-signal `_HEADERS` replaces the entire common set. An empty per-signal value selects no headers. The parser accepts up to 16 distinct pairs and 8,192 characters in the input. Each header name is limited to 64 characters and each decoded value to 2,048 printable ASCII characters. |
| `OTEL_TRACES_SAMPLER` and `OTEL_TRACES_SAMPLER_ARG` | Default `parentbased_traceidratio` with root ratio `0.1`. Set a decimal ratio from `0` to `1`. For a short diagnostic window, choose `parentbased_always_on` and omit the argument. |
| `OTEL_METRIC_EXPORT_INTERVAL` | Integer milliseconds, 1,000–3,600,000, default 30,000. Review alert windows when changing it. |
| `OTEL_SERVICE_NAME` and `OTEL_RESOURCE_ATTRIBUTES` | Service name defaults to `scaleset-aws-controller` and accepts up to 128 characters. The supported resource override is `deployment.environment.name`, up to 64 characters, default `development`. Names permit ASCII letters, digits, period, underscore, and hyphen. Set the tier explicitly in deployed environments. |

Use absolute HTTP or HTTPS URLs without user info, query strings, or fragments.
Use HTTPS for remote receivers. For HTTP/protobuf a common base URL can include
a path prefix. The controller appends `/v1/traces`, `/v1/metrics`, and
`/v1/logs` to it. A per-signal HTTP/protobuf endpoint is the complete request
URL. For gRPC use a base URL, including any routing prefix your receiver
requires. The controller appends the signal's gRPC service method. Set the
protocol explicitly when testing. [OTLP exporter specification](https://opentelemetry.io/docs/specs/otel/protocol/exporter/)

### Probe an HTTP/protobuf receiver

With a receiver already listening on loopback, run from the repository root.
Use a separate staging receiver or pipeline for diagnostic traffic.

```sh
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf \
  dotnet run --project src/Knightfall.ScaleSet.Aws.Controller -- \
  telemetry-smoke --otlp-endpoint http://127.0.0.1:4318
```

The command sends a correlated trace and log plus a metric without AWS or
GitHub credentials. It requires all three exports and samples its test span.
Exit code 2 means invalid, disabled, or incomplete configuration. Exit code 1
means export or shutdown failed, including a rejected payload, invalid
response, missing receipt, or timeout. Exit code 0 means the receiver accepted
all three signals. An accepted response with a warning and zero rejected
records also exits zero. Inspect the receiver's downstream pipeline and query
the backend separately to confirm usable data. [OTLP response semantics](https://opentelemetry.io/docs/specs/otlp/)

### Configure ECS and header secrets

Save the following as a strict JSON file such as `telemetry.json`. Use the
account, Region, receiver DNS name, and exact secret ARN for the deployment.
This example uses gRPC. Test a TLS listener that supports HTTP/2.

```json
{
  "serviceName": "scaleset-aws-controller",
  "deploymentEnvironment": "production",
  "metricExportIntervalMs": 30000,
  "sampler": { "type": "parentbased_traceidratio", "ratio": 0.1 },
  "exporters": {
    "common": {
      "endpoint": "https://otel.example.net:4317",
      "protocol": "grpc",
      "timeoutMs": 3000,
      "headersSecret": {
        "secretArn": "arn:aws:secretsmanager:ap-southeast-2:111122223333:secret:scaleset-otlp-headers-a1b2c3",
        "kmsKeyArn": "arn:aws:kms:ap-southeast-2:111122223333:key/12345678-1234-1234-1234-123456789012"
      }
    }
  }
}
```

For an HTTP/protobuf ECS receiver, replace the `exporters` block with the
following shape. This example sends all signals to the common receiver and
routes metrics to an explicit URL. The credentials for both URLs must be
valid on their respective receivers. If metrics need different credentials,
add a `headersSecret` under `metrics`.

```json
{
  "exporters": {
    "common": {
      "endpoint": "https://otel.example.net:4318/otlp",
      "protocol": "http/protobuf",
      "timeoutMs": 3000,
      "headersSecret": {
        "secretArn": "arn:aws:secretsmanager:ap-southeast-2:111122223333:secret:scaleset-otlp-headers-a1b2c3"
      }
    },
    "metrics": {
      "endpoint": "https://metrics.example.net:4318/ingest/v1/metrics"
    }
  }
}
```

The typed JSON accepts `sdkDisabled`, `serviceName`, `deploymentEnvironment`,
`metricExportIntervalMs`, `sampler`, and `exporters`. Under `exporters`, use
`common`, `traces`, `metrics`, or `logs`. Each can carry `endpoint`,
`protocol`, `timeoutMs`, and `headersSecret`. An optional
`headersSecret.kmsKeyArn` identifies the exact customer-managed KMS key.
Omit it when the secret uses the AWS-managed Secrets Manager key.
The secret and key ARNs must match the deployment's AWS partition, account, and
Region. CDK rejects unknown fields, duplicate keys, literal header values, and
partial secret ARNs.

Use HTTPS for every remote exporter that sends headers. This applies to
both protocols and to signal endpoints that inherit the common headers. HTTP
with headers is supported for `localhost` and literal loopback addresses used
in local smoke tests. CDK checks secret references, and the controller validates
the resolved endpoint and headers before registering the exporters.

Store the complete header list as the Secrets Manager secret string, using
the same format as `OTEL_EXPORTER_OTLP_HEADERS`, for example
`authorization=Bearer%20REPLACE_ME`. Supply the actual value through your
secret provisioning workflow. ECS injects the value into the controller only
at task startup. CDK grants the **execution role** access to the exact
referenced secrets and scopes optional KMS decrypt by Secrets Manager service
and secret ARN. Check ECS task startup events if injection fails. After
rotation, use the planned controller replacement procedure so the replacement
reads the new value. Keep values out of source, synthesized templates,
commands and command history, logs, and runner environments. [ECS Secrets Manager injection](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/secrets-envvar-secrets-manager.html)

Supply the same telemetry JSON to synth, diff, and the guarded deploy. For
example, from the repository root after `npm --prefix cdk ci`:

```sh
(
  cd cdk
  ./node_modules/.bin/cdk synth --strict --no-lookups \
    --app 'node bin/controller.ts --config ../controller.yaml --generation 42 --telemetry ../telemetry.json'
)
```

The deploy command in the [operator runbook](operator-runbook.md) also accepts
`--telemetry ../telemetry.json` with `npm --prefix cdk run deploy:controller`.
Verify the synthesis contains environment settings and secret ARN references
on the controller, plus scoped execution-role grants. Use the approved
generation and exact runtime configuration in the guarded deployment.
Keep a known-good telemetry settings file with the deployment record so an
application rollback can restore the intended receiver and sampling.

## Build dashboards and alerts

Use the logical OTLP instrument names below. Map names, units, resource fields,
and histogram quantiles to your backend's ingestion model. Preserve
`service.namespace=knightfall.scaleset.aws`, `service.name`,
`deployment.environment.name`, and `scaleset.deployment.name`. Include
`cloud.account.id` and `cloud.region` to distinguish same-named deployments.
Keep `service.instance.id` until after freshness and ownership selection, and
display `profile` and `provider` for profile-scoped instruments. Smoke records
lack deployment and cloud identity. [OpenTelemetry deployment resource convention](https://opentelemetry.io/docs/specs/semconv/resource/deployment-environment/)

For each expected profile in the deployed configuration and catalog, select
latest gauges from the same controller instance. A fresh, ready instance is
eligible for a current-capacity view. A concurrent older instance can leave
historical last values in the store. Display an unknown state if several
instances appear current, and check the durable lease and ECS ownership to
resolve it. Keep explicit zeroes for valid empty observations. Leave absent
gauges empty and indicate missing evidence. Exclude catalog tombstones and
disabled profiles from expected-presence alerts. A retired profile stops
emitting entirely, while ownership loss clears authoritative capacity and
can leave readiness zero.

The defaults are a five-second heartbeat and reconciliation retry, a
45-second lease, a 20-second profile pass budget, a two-minute controller
sample validity, and a 30-second metric export interval. The planner can
consider an input observation for up to ten minutes. Check the separate
`inventory.age` and `reconcile.age` gauges when displaying old inputs. Treat
these windows as starting thresholds. Scale silence and sustained alerts to at
least three actual metric export intervals and tune the others to the target's
normal load and GitHub poll times. Compare counts as reset-aware counter
increases or rates. Filter histograms by operation before computing percentiles.


For example, select readiness and capacity with the following logical joins.
Let `E` be the configured export interval.

```text
fresh_ready = latest(scaleset.aws.profile.ready) == 1
              and age_of_last_metric_receipt <= 2 * E
current_capacity = latest(capacity_gauge)
                   where age_of_this_gauge_receipt <= 2 * E
                   joined to fresh_ready on deployment, profile, provider, instance
profile_missing = expected_profile without a recent readiness sample
export_silent = age_of_last_expected_metric_receipt > 3 * E
```

Apply the sustained interval to the alert condition per profile. Preserve
instance identity on counters so a replacement's reset does not look like a
decrease in work or a sudden failure spike.

| View | Query recipe | Starting alert |
| --- | --- | --- |
| Profile capacity | Plot latest `scaleset.aws.capacity.assigned`, `.active`, `.idle`, `.starting`, `.unknown`, and `.deficit` per profile and owner. Add `.configured_max`, `.configured_idle_reserve`, `scaleset.aws.profile.ready`, `scaleset.aws.inventory.age`, and `scaleset.aws.reconcile.age`. Use zero for an observed count of zero. | Ready is zero or absent for 2 minutes for an expected profile. On a fresh owner, alert on positive deficit or inventory older than 2 minutes for 2 minutes. Show stale capacity as unknown. |
| GitHub listener | Compute p95 `scaleset.aws.listener.operation.duration` for `operation=poll` and separately for `operation=acknowledge`. Chart increases in `scaleset.aws.listener.operations` for `admission-reserve`, `admission-settle`, `checkpoint-settled`, and `session-quarantine`, split by outcome. Plot `scaleset.aws.listener.cooldown.remaining`. | Investigate three or more failed, unknown, or fenced attempts in 5 minutes, sustained cooldown with pending demand, or p95 acknowledgement over 2 seconds for 5 minutes with sufficient traffic. Establish a poll baseline from the target's long-poll behavior. Ordinary empty polls and one retry are routine. |
| Released GitHub Client | Chart request/error rate and p95 using `scaleset.client.requests` and `scaleset.client.request.duration` by `dependency`, `method`, and `status_class`. Chart `scaleset.client.retries` by `dependency`, `method`, and `retry_kind`. These are process-level measurements from `ScaleSet.Client`. | Start with errors above 5% of requests for 5 minutes with at least 20 requests, tracking `5xx` and `none` separately from `4xx`. Compare retries against a rolling baseline and correlate sustained increases with the per-profile AWS listener measurements. The released Listener contributes spans. |
| AWS execution | Plot `scaleset.aws.provider.operations` and p95 `scaleset.aws.provider.operation.duration` for `run_instances`, `run_instances_failover`, `terminate_instances`, `run_task`, and `stop_task`. Filter readbacks separately. Plot increases in `scaleset.aws.ec2.az_failovers` and `scaleset.aws.ec2.template_drifts`. | Investigate three or more provider failures or unknown outcomes in 5 minutes, or recurring drift while deficit persists. For Fargate replay use the sampled DynamoDB `fargate-replay` operation and its `replay_window` or `incomplete_readback` reason with the exact journal deadline and task readback. |
| Handoff and startup | Chart `scaleset.aws.handoff.operations` and `.duration` for `advance` and the persistence operations `offer.publish`, `offer.consume`, `offer.revoke` when present. Filter `scaleset.aws.startup.observations` and `.duration` with `operation=authorize` for controller-observed startup authorization. | Investigate three or more confirmed failures in 5 minutes, or held recovery throughout that window while demand persists. An authorized startup or confirmed offer establishes that stage. Consult the runner's sanitized console and exact GitHub and provider records for readiness, exit, or completion evidence. |
| Durability and ownership | Plot increases in `scaleset.aws.lease.losses`, `scaleset.aws.dynamodb.conflicts`, and `scaleset.aws.effect.rebuild.pages`. Pair rebuild progress with fresh `scaleset.aws.effects.unfinished` and `scaleset.aws.cleanup.oldest_age`. Inspect fenced listener outcomes and sampled DynamoDB lease and effect activities. | Investigate two or more lease losses in 10 minutes, conflicts with stalled work, or unfinished effects that fail to decrease for 5 minutes while their evidence is current. Escalate eligible cleanup above 5 minutes sustained. Read the exact effect and retain ambiguous evidence. |
| Runtime and export | Plot `dotnet.process.memory.working_set`, `dotnet.thread_pool.queue.length`, and `dotnet.gc.pause.time` against the task's memory and load. From the receiver or backend, track last receipt by environment, deployment, and expected signal. Count sanitized export and queue diagnostics from the ECS console sink. | Alert if expected metrics cease for at least three configured export intervals, 90 seconds at the default. Check receiver-side failures concurrently. Investigate working set above 80% of the task limit for 5 minutes. Trace and log silence can be normal at low traffic. Use a known probe or independently scheduled ingest check for a signal expected to be quiet. |

Use AWS-owned listener metrics for per-profile GitHub analysis. Provider
attempt counters include retries and separate readbacks, so filter the exact
mutation operation when estimating traffic. The Fargate replay decision
remains tied to its durable 50-minute cutoff and write reserve. Sampled trace
counts cannot measure the rate of all cutoff decisions. Use the exact
journal during recovery.

## Diagnose missing or degraded telemetry

1. Check the controller's ECS service, local `/healthz` and `/readyz` evidence,
   and the current lease. The ECS probe checks `/healthz` on loopback.
   Readiness depends on current authority and profile evidence. A healthy
   process can continue lifecycle work while OTLP exports fail.
2. Check the enabled signals, protocol, URL paths, and sampler. From an
   approved diagnostic process on the same network path, check direct DNS,
   routing, security groups, TLS trust and hostname, and HTTP/2 for gRPC.
   The exporter uses direct connections and disables redirects. Check that
   the ingress routes each OTLP service or HTTP path to a compatible receiver.
3. For rejection or timeout, check receiver logs and internal telemetry.
   Verify header authorization through the secret reference and task startup,
   then distinguish ingress acceptance from downstream exporter loss,
   throttling, and backend retention or query filters. Run `telemetry-smoke`
   with the same transport and approved credentials to probe all signals.
   Use synthetic records when enabling verbose receiver diagnostics.
   [Collector troubleshooting](https://opentelemetry.io/docs/collector/troubleshooting/)
4. Start from the affected deployment, profile, provider, controller instance,
   and time range. Query the allowlisted operation, outcome, and reason in
   logs. Follow a present trace ID and span ID to the sampled operation and
   dependency spans. Parent-based head sampling can leave a correlated log
   without a stored trace. Use the same fields and time window in the
   sanitized console logs, then verify the lifecycle row and provider
   readback if the trace is unavailable.

The controller rate-limits redacted export diagnostics to at most one write
per five seconds. They can report rejection, malformed replies, an unavailable
receiver, timeouts, queue drops, and incomplete shutdown. Keep a receiver-side
alert path independent of the controller's own exports.

## Privacy, budgets, and target qualification

Use the configured profile/provider and the instrument's bounded
operation/outcome/reason labels. Leave workflow and job IDs, runner and
instance or task identifiers, definition revisions, routing labels, config
digests, URLs, and trace IDs outside metric dimensions. Plan series per
instance from the configured profiles and actual label combinations for each
instrument. Track retained series across controller replacements separately.
The SDK caps metric streams at 128 and cardinality at 2,000 per configured
stream. Budget below those limits and review distinct series in a target soak.

The telemetry path caps pending spans and logs at 2,048 each with batches of
128, local console records at 256, and receiver responses at 64 KiB. Sanitized
structured records use allowlisted scalar fields. HTTP paths and bodies,
headers, credentials, session queue URLs, JIT data, and raw exception strings
are excluded from exported diagnostic detail. Keep separate controls on
backend access, retention, and exported field indexing. The exporter drops
telemetry when bounded capacity is exhausted and shares one five-second
shutdown budget across signals. Collector failure never authorizes a lifecycle
mutation or changes acknowledgement, recovery, or lease decisions.

The local checks exercise both transports, exact captured payloads, partial
and disabled configuration, bounded privacy and outage behavior, and scoped
ECS synthesis. The mixed local scenario uses two distinct EC2 Launch Templates
and two Fargate task definitions and clusters. It checks profile attribution,
takeover and retirement freshness, and continued lifecycle progress through
receiver failures. Separate release qualification should verify the target
private-network path, real secret injection and rotation, TLS and receiver
authorization, backend query visibility and retention, and delivery of a
silence alert. Record the controller image digest, task definition, deployed
generation, receiver and transport, observed signals, and alert timestamps in
the [qualification record](emulator-boundaries.md). Keep AWS, GitHub scope,
and runner OS qualifications alongside their own evidence.
