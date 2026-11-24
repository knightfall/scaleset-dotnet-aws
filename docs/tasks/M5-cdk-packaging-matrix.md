# M5: CDK, Observability, Controller Packaging, and Synthesis Matrix (T18–T21)

## T18: Compose shared TypeScript CDK infrastructure and IAM

**Depends on:** [T03](M1-foundation-config-planner.md#t03-define-v1alpha1-schemas-and-typescript-config-compiler), [T10](M2-lifecycle-sessions-handoff.md#t10-implement-lifecycle-reconciliation-dispatch-and-profile-retirement), [T11](M2-lifecycle-sessions-handoff.md#t11-implement-cryptographic-one-job-jit-handoff), [T12](M3-direct-ec2-execution.md#t12-implement-direct-ec2-preflight-and-read-only-inventory), [T15](M4-fargate-execution.md#t15-implement-fargate-runner-preflight-and-read-only-inventory).

### Deliverable

Build the standalone TypeScript AWS CDK v2 entry point (`cdk/bin/controller.ts`) and stack constructs (`cdk/lib/`) composing the shared DynamoDB table, versioned S3 config artifact, conditional runner EventBridge/SQS integrations, least-privilege IAM policies, and runtime retirement preflight gating.

### Requirements

- **Tracking table & runtime config**:
  - `create`: provision encrypted retained DynamoDB table `<metadata.name>-runner-lifecycle` (`PK`/`SK`, `due` `KEYS_ONLY` GSI on `dueShard`/`dueAtMillis`, `expiresAt` TTL, PITR enabled). `import`: reference existing table without mutation.
  - Upload deterministic runtime JSON from [T03](M1-foundation-config-planner.md#t03-define-v1alpha1-schemas-and-typescript-config-compiler) as a versioned S3 asset; pass exact S3 URI, object version ID, SHA-256 digest, and monotonic `--generation` to the controller launch command.
- **Conditional runner resources & IAM separation**:
  - Only when `active`/`draining` `ec2` profiles exist: EventBridge `EC2 Instance State-change Notification` rules scoped to the deployment account and Region -> SQS + DLQ, scoped `ec2:RunInstances`/`TerminateInstances`/`CreateTags` + `iam:PassRole` on selected Launch Templates/subnets/bootstrap roles, and `dynamodb:LeadingKeys` bootstrap policy (`HELLO#`, `OFFER#`, and proven `STATUS#`).
  - Only when `active`/`draining` `fargate` profiles exist: EventBridge `ECS Task State Change` rules -> SQS + DLQ, scoped `ecs:RunTask`/`StopTask`/`TagResource` + `iam:PassRole` (`iam:PassedToService: ecs-tasks.amazonaws.com`), and `dynamodb:LeadingKeys` bootstrap policy (`HELLO#`, `OFFER#` only).
  - Deduplicate `ecsClusters` `create` entries if controller host and Fargate runners share a cluster. Never grant runner `ecs:RunTask`/`StopTask` when only controller hosting uses ECS.
  - In `dry-run` mode: omit GitHub `secretsmanager:GetSecretValue`, DynamoDB mutations, SQS receive/delete, and all EC2/ECS runner launch/stop/`PassRole` permissions.
- **Runtime retirement preflight gate**:
  - Require `preflight retire` verification against the DynamoDB table's catalog/generation/tombstones (verified against MiniStack / AWS Emulator DynamoDB in `T25` and targeting the deployment table at deploy time) before a CDK deployment removes IAM grants or EventBridge rules for a removed/disabled profile; reject fixture/context-flag bypasses.

### Deployment command

Use the AWS CLI v2 and the repository's Release build of the controller. The deployment caller needs permission to read the current stack outputs, the exact previous S3 object version, and the deployment table. It also needs permission to prepare and execute CloudFormation change sets for the selected stack.

```sh
npm --prefix cdk run deploy:controller -- --config /path/to/controller.yaml --generation 42
```

The command compiles the candidate once. For an existing stack, it reads the current S3 version and verifies its digest and table against the current stack outputs. It runs `preflight retire` before preparing a change set and again before executing that exact change set. New stacks require confirmed stack absence and either a new table name or an existing imported table with an empty deployment partition. An active-to-dry-run update removes active grants and requires the same retirement checks.

Use the deployment command for stack updates. Direct CDK or CloudFormation deployment requires independent enforcement of the same gate. Offline synthesis uses `node bin/controller.ts --config <controller.yaml> --generation <generation>` as the CDK app under `cdk synth --strict --no-lookups`.

The stack supplies the controller task role, host cluster identity, and `run` arguments to the T20 host construct. The runtime object copy must return a destination version ID. The bounded artifact waiter gives initial S3 versioning up to 16 minutes to settle. [S3 copy response](https://docs.aws.amazon.com/AmazonS3/latest/API/API_CopyObject.html) · [S3 versioning configuration](https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-properties-s3-bucket-versioningconfiguration.html)

The EC2 rule uses the fields available in the native event. The controller verifies ownership in AWS readback and checks subnet and AZ against the launch intent. Bootstrap roles receive their own `HELLO#` and `OFFER#` permissions. An EC2 `STATUS#` grant requires the T14 and T26 isolation qualification. [EC2 event payload](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/monitoring-instance-state-changes.html)

### Acceptance

1. `cdk synth --strict --no-lookups` succeeds offline for every valid config permutation with logical IDs invariant to YAML profile ordering.
2. Premature permission removal without verified retirement proof fails the preflight check.

---

## T19: Implement OpenTelemetry tracing, metrics, and OTLP logging

**Depends on:** [T04](M1-foundation-config-planner.md#t04-validate-runtime-json-and-conditional-provider-factories), [T10](M2-lifecycle-sessions-handoff.md#t10-implement-lifecycle-reconciliation-dispatch-and-profile-retirement), [T11](M2-lifecycle-sessions-handoff.md#t11-implement-cryptographic-one-job-jit-handoff), [T13](M3-direct-ec2-execution.md#t13-implement-direct-ec2-launch-and-targeted-cleanup), [T16](M4-fargate-execution.md#t16-implement-standalone-fargate-runtask-launch-and-cleanup).

### Deliverable

Implement end-to-end OpenTelemetry tracing (`ActivitySource`), metrics (`Meter`), and OTLP structured logging (`ILogger<T>` -> Serilog OTLP sink) across all `Knightfall.ScaleSet.Aws.*` assemblies, along with bounded-cardinality/redaction enforcement, the `telemetry-smoke` CLI diagnostic command, and loopback OTLP collector verification.

Implement the [OpenTelemetry integration plan](../opentelemetry-integration.md), which defines the precise source/meter inventory, metric catalog, resource and profile attribution, configuration precedence, delivery slices, and captured-payload acceptance suite. T19 owns telemetry; T20/T21 own host/deployment wiring and T25/T26 own integrated evidence and operator guidance. The dependency graph is unchanged.

### Requirements

- **Library vs executable separation (`scaleset.NET` D16/D17/D20)**:
  - Reusable libraries (`Core`, `DynamoDb`, `GitHub`, `Providers.Ec2`, `Providers.Fargate`, `Handoff`, `RunnerBootstrap`) emit only `Microsoft.Extensions.Logging.ILogger<T>`, `System.Diagnostics.ActivitySource`, and `System.Diagnostics.Metrics.Meter` without initializing Serilog or the OpenTelemetry SDK.
  - `Knightfall.ScaleSet.Aws.Controller` registers `Serilog.Extensions.Hosting` + `Serilog.Sinks.OpenTelemetry` + console sink for logs (with no duplicate OpenTelemetry `ILogger` exporter) and `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Exporter.OpenTelemetryProtocol` + approved HTTP/Runtime instrumentation. Subscribe to `ScaleSet.Client`, `ScaleSet.Listener`, and explicit `Knightfall.ScaleSet.Aws.*` activity sources, plus Client/application meters. The released Listener has no meter; listener measurements belong to AWS adapters.
  - Unify OTel resource attributes (`service.name`, `service.version`, `service.instance.id`, `deployment.environment.name`, deployment identity). Logs correlate to traces through `TraceId`/`SpanId`; metrics correlate through resource/profile/time and verified exemplars, never trace-ID labels.
- **Instrumentation inventory**:
  - Emit correlated spans for listener poll/delivery/acquire/ack loops (parenting `ScaleSet.Client` activities), per-profile reconciliation cycles, DynamoDB transactional intents and `EFFECT-REBUILD` passes, JIT handoff publish/consume/revoke, Direct EC2 `RunInstances`/`TerminateInstances`/Launch Template resolution, Fargate `RunTask`/`StopTask`, and SQS/EventBridge processing.
  - Emit counters, gauges, and histograms for poll/ack latency, rate-limit cooldowns, per-profile `assigned`/`active`/`idle`/`starting`/`unknown`/`deficit`/`drift`/`orphan_candidates`/`cleanup_age_seconds`/`lease_loss`/`effect_rebuild_pages`/`unfinished_effects`, handoff and startup latency, EC2 AZ failover and `$Latest` drift events, launch/cleanup outcomes, and DynamoDB transaction conflicts.
- **Bounded cardinality & secret redaction**:
  - Restrict AWS metric dimensions to the per-instrument bounded subset of `profile`, `provider`, `operation`, `outcome`, and `reason`, with environment on the resource. Maintain separate allowlists for the Client SDK's bounded `dependency`, `method`, `status_class`, and `retry_kind` tags and approved HTTP/runtime instruments. Forbid `job_id`, `request_id`, `runner_name`, `runner_id`, `instance_id`, `task_arn`, and `trace_id` as metric label keys.
  - Scrub GitHub App private keys, PATs, exchanged tokens, signed queue URLs, and plaintext JIT from all logs, span attributes, and exception events.
- **Config validation, outage isolation, shutdown, and `telemetry-smoke`**:
  - Validate `--otlp-endpoint` and supported `OTEL_*` settings using the plan's single precedence/resource model. No endpoint means console-only; invalid supplied configuration fails before cloud work. The receiving collector/sink is operator-supplied (do not bundle an always-on collector sidecar).
  - Ensure an unreachable, slow, or rejecting OTLP receiver never stalls reconciliation loops, GitHub message acknowledgements, or DynamoDB lease renewal.
  - Bound telemetry flush and disposal to one shared `<= 5s` deadline inside the `60s` total SIGTERM shutdown budget (`T20`), with bounded queues/retries and suppression of exporter self-instrumentation.
  - Provide `telemetry-smoke` CLI command emitting correlated logs, traces, and metrics to the configured OTLP endpoint without AWS or GitHub credentials.

### Acceptance

1. Loopback OTLP receiver tests verify correlated logs, traces (including `ScaleSet.Client` and `ScaleSet.Listener` spans), and metrics arrive with matching resource attributes and zero duplicate log or span exports.
2. Cardinality and redaction tests fail on any forbidden high-cardinality metric tag or synthetic secret/JIT leakage in captured OTLP payloads.
3. Fault tests prove OTLP receiver outage/hang never stalls reconciliation/acks and respects the bounded shutdown flush timeout.
4. `dotnet run --project src/Knightfall.ScaleSet.Aws.Controller -- telemetry-smoke` succeeds offline against the loopback OTLP receiver with zero cloud credentials.
5. Complete the integration plan's [captured-payload acceptance suite](../opentelemetry-integration.md#delivery-slices-and-acceptance-evidence), including both transports, disabled/partial configuration, four-profile cardinality, ownership/retirement freshness, saturation/recovery, and unchanged lifecycle decisions with telemetry disabled.

---

## T20: Build controller runtime, OCI container, and ECS Fargate host

**Depends on:** [T04](M1-foundation-config-planner.md#t04-validate-runtime-json-and-conditional-provider-factories), [T07](M2-lifecycle-sessions-handoff.md#t07-implement-ecs-lease-ownership-lineage-and-effect-journal), [T10](M2-lifecycle-sessions-handoff.md#t10-implement-lifecycle-reconciliation-dispatch-and-profile-retirement), [T18](#t18-compose-shared-typescript-cdk-infrastructure-and-iam), [T19](#t19-implement-opentelemetry-tracing-metrics-and-otlp-logging).

### Deliverable

Wire `Knightfall.ScaleSet.Aws.Controller`, native `.NET 10` OCI image publishing (`dotnet publish /t:PublishContainer`), health probes, and the CDK 1-replica ECS Fargate controller service.

### Requirements

- **CLI entry-point contract**:
  - `config verify --file <path> [--sha256 <hex>]` — offline schema/semantic verification with zero AWS/GitHub calls.
  - `telemetry-smoke` — offline OTLP signal verification per [T19](#t19-implement-opentelemetry-tracing-metrics-and-otlp-logging) with zero AWS/GitHub calls.
  - `reconcile --dry-run --once` — local `--config` or S3 (`--config-s3-uri`, `--config-version-id`, `--sha256`) read-only dry-run report.
  - `run` — requires verified config + digest + `--generation`, optional `--ec2-event-queue-url` / `--ecs-event-queue-url` (rejected if backend not enabled), private `--health-addr`, and optional `--otlp-endpoint`.
  - `healthcheck` — bounded loopback probe against `/healthz` for the ECS container `CMD` health check.
  - `preflight table | retire | clock` — read-only environment schema/PITR/TTL check, `T18` retirement gate verification, and trusted host clock-offset check before mutation or takeover.
- **Native `.NET 10` OCI container (no Docker daemon)**:
  - Publish non-root, read-only-rootfs chiseled Linux (`linux-x64` / `linux-arm64`) OCI images directly via `dotnet publish src/Knightfall.ScaleSet.Aws.Controller/Knightfall.ScaleSet.Aws.Controller.csproj --configuration Release /t:PublishContainer`, embedding `cdk/config/schema/*.json` and excluding secrets/build caches.
- **Health probes and shutdown**:
  - Compose the [T19](#t19-implement-opentelemetry-tracing-metrics-and-otlp-logging) telemetry pipeline using the [host configuration contract](../opentelemetry-integration.md#configuration-contract), one shared bounded flush/disposal deadline (`<= 5s`), and no cloud-client initialization for `telemetry-smoke`.
  - Expose loopback `/healthz` (process/supervision progress — one degraded profile does not cause restart loops) and `/readyz` (config, lease, and per-profile state).
  - Enforce a **60-second total** SIGTERM-to-exit shutdown budget inside a **90-second** ECS `stopTimeout`.
- **CDK ECS Fargate host construct**:
  - Synthesize 1 desired replica, `minHealthyPercent: 0`, `maxHealthyPercent: 100`, deployment circuit breaker, private subnets, read-only rootfs, ECS Exec disabled, separate controller task/execution roles, and scoped `ecs:DescribeTasks` on the controller cluster so a successor controller verifies the prior controller task ARN reached `STOPPED`.

### Acceptance

1. `dotnet publish /t:PublishContainer` builds the OCI image without a Docker daemon; image config asserts non-root user, read-only rootfs compatibility, embedded schemas, and working `healthcheck`.
2. Root composition tests prove graceful 60s SIGTERM shutdown during active polling/effects, zero unselected backend calls, and failover block while prior controller task ARN is not `STOPPED`.

---

## T21: Verify full offline CDK and IAM synthesis matrix

**Depends on:** [T18](#t18-compose-shared-typescript-cdk-infrastructure-and-iam), [T19](#t19-implement-opentelemetry-tracing-metrics-and-otlp-logging), [T20](#t20-build-controller-runtime-oci-container-and-ecs-fargate-host).

### Deliverable

Implement `npm --prefix cdk run test:synth-matrix` exercising every valid deployment matrix permutation offline with strict positive and negative resource/IAM assertions.

### Matrix dimensions

- **Runner backends**: `ec2`-only (`pinned` & `$Latest` alias), `fargate`-only, `mixed`, plus disabled extra profiles.
- **Controller hosting**: `ecs-fargate` (host-only cluster import/create, shared with Fargate runners, separate from Fargate runners).
- **Table & mode**: `create` vs `import`; `dry-run` (with/without `inspectAws`) vs `active`.
- **Lifecycle transitions**: YAML-only profile addition, `active -> draining`, verified retirement removal, blocked premature removal, and last-profile rejection.

### Critical negative assertions

- **EC2-only on ECS Fargate host**: controller host cluster/service/task-definition only; **zero** runner `ecs:RunTask`, `ecs:StopTask`, runner `TagResource`, or runner ECS Task EventBridge rules.
- **Fargate-only**: **zero** `ec2:RunInstances`, `ec2:TerminateInstances`, `ec2:CreateTags`, or EC2 Instance State-change EventBridge rules.
- **Dry-run**: **zero** Secrets Manager value reads, DynamoDB writes, SQS permissions, runner launches/stops, or `iam:PassRole`.
- **Observability wiring**: Assert the [host configuration contract](../opentelemetry-integration.md#configuration-contract) maps explicit deployment properties to controller environment variables and secret references; credential values never appear in synthesized artifacts, secret injection permissions belong to the execution role, and no runner permissions are added. Confirm zero bundled Collector sidecar containers.
- **Non-destructive imports**: **zero** CloudFormation replacement or mutation of imported VPCs, subnets, security groups, Launch Templates, task definitions, tables, or clusters.

### Acceptance

1. `npm --prefix cdk run test:synth-matrix` and `make check` pass offline in CI, printing row counts and verified template coverage.
