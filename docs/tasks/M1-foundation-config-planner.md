# M1: Foundation, Configuration, and Planner (T01–T05)

## T01: Pin build tooling, Roslynator, and CDK baseline

**Depends on:** Merged planning-baseline PR.

### Deliverable

Establish the deterministic `.NET 10` solution skeleton (`Knightfall.ScaleSet.Aws.slnx`), centralized package management, pinned Roslynator analyzer policy, TypeScript CDK toolchain, and CI entry points (`Makefile` and `.github/workflows/ci.yml`).

### Requirements

- Pin `.NET 10` SDK in `global.json` (`10.0.400`, `rollForward: latestPatch`, `test.runner: Microsoft.Testing.Platform`).
- Configure `Directory.Build.props` with `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`, `AnalysisLevel=latest-recommended`, `Deterministic=true`, `RestorePackagesWithLockFile=true`, `NuGetAudit=true`, `NuGetAuditMode=all`, and `NuGetAuditLevel=moderate`.
- Configure `Directory.Packages.props` with `ManagePackageVersionsCentrally=true`, `Roslynator.Analyzers` (`5.0.1`, `PrivateAssets=all`), `xunit.v3` (`4.0.3`), and exact versions for logging/hosting, Serilog, OpenTelemetry, and AWS SDK for .NET v4 packages (`AWSSDK.*`). Use the [released SDK dependency baseline](../dependency-baseline.md) to select compatible versions; do not force `Microsoft.Extensions.Logging.Abstractions` below the SDK's `11.0.0` dependency. Record the resolved versions and lock files in this task; no floating versions.
- Pin published `Knightfall.ScaleSet.Client` and `Knightfall.ScaleSet.Listener` from NuGet.org to the exact version in the [released dependency baseline](../dependency-baseline.md). Exclude local candidate feeds and sibling project references from the reproducible build. Do not reference `Knightfall.ScaleSet.Providers.Docker` or `Knightfall.ScaleSet.Hosting`.
- Pin Node version in `.node-version`, `cdk/package-lock.json`, `cdk/tsconfig.json`, `cdk/eslint.config.mjs`, and `cdk/.prettierignore`.
- Define `Makefile` `check` target running locked restore, Release build, Release test execution, and CDK lint/typecheck/test/synth.

### Acceptance

1. Demonstrate during setup that a deliberate Roslynator diagnostic fails `dotnet build Knightfall.ScaleSet.Aws.slnx --configuration Release`, then remove the probe and commit the clean `.editorconfig` and lock files (`packages.lock.json`).
2. `make check` succeeds cleanly in CI with `--locked-mode` and zero warnings.

---

## T02: Verify ScaleSet Client and AWS SDK v4 contracts

**Depends on:** [T01](#t01-pin-build-tooling-roslynator-and-cdk-baseline).

### Deliverable

Implement `tests/Knightfall.ScaleSet.Aws.SdkContract.Tests` validating every GitHub and AWS SDK operation, serialization rule, retry setting, and error classification relied upon by this plan against pinned packages and offline fake HTTP/wire transports.

### Requirements

- **GitHub (`Knightfall.ScaleSet.Client`)**:
  - Verify `GitHubTarget.Parse` + `ScaleSetCredentials` accept GitHub.com and `*.ghe.com` org/repo (App or PAT) and enterprise (`github.com/enterprises/{slug}` classic PAT), reject enterprise App credentials, and throw `NotSupportedException` for custom GHES hosts (`scaleset.NET` `#20`).
  - Verify `ScaleSetRequestRetry` enforces **one wire attempt** on non-idempotent mutations (`CreateScaleSetAsync`, `GenerateJitConfigAsync`, `CreateMessageSessionAsync`) when a fake transport commits server-side then drops the HTTP response.
  - Verify `ScaleSetRateLimitException` (`RetryAfter`, reset timestamps), `ScaleSetApiException` strict JSON `null`/trailing-garbage rejection, 64-bit ID preservation, and redaction of tokens/private keys/session URLs.
  - Verify `DeleteMessageSessionAsync` sends the exact journaled scale-set ID/session GUID using administration credentials, does not create a session, and preserves lost-response, 400 and 404 uncertainty without an automatic DELETE retry.
- **Released Listener (`Knightfall.ScaleSet.Listener`)**:
  - Restore the exact released Client/Listener packages in an isolated cache and verify their source/provenance identities according to the [dependency baseline](../dependency-baseline.md). Compile a consumer with custom checkpoint, handler, and execution-policy implementations and no Hosting, Docker, or AWS dependency in that consumer.
  - Verify receipt-before-handler ordering, replay with original observation time, `Processed` acknowledgement-only resume, and `Acknowledging` recovery without polling or blindly retrying DELETE. Verify fixed ownership cancellation, graceful-stop completion, and rejection of a changed session GUID before retry.
- **AWS SDK for .NET v4**:
  - Verify `Amazon.DynamoDBv2` `TransactWriteItemsRequest.ClientRequestToken` handling: inspect whether AWS SDK v4 auto-populates a UUID; document and enforce explicit per-transaction token generation if absent. Verify `MaxErrorRetry = 0` configuration on journal-owned writes and exact mapping of `TransactionCanceledException` cancellation reasons and `DeleteItem(ReturnValues=ALL_OLD)` pre-image extraction.
  - Verify `Amazon.EC2` `RunInstances`, `TerminateInstances`, `DescribeLaunchTemplateVersions`, `DescribeInstances`, `DescribeImages`, `DescribeSubnets`, `DescribeSecurityGroups`, and `CreateTags` wire serialization and `MaxErrorRetry = 0` write configuration.
  - Verify `Amazon.ECS` `RunTask` (including partial `Failures` on HTTP 200), `StopTask`, `ListTasks`, `DescribeTasks`, `DescribeClusters`, `DescribeTaskDefinition`, `ListAccountSettings`, and `MaxErrorRetry = 0` write configuration.
  - Verify `Amazon.IdentityManagement` (`GetInstanceProfile`, `GetRole`), `Amazon.SecretsManager`, `Amazon.S3`, `Amazon.SQS`, and `Amazon.SecurityToken` (`GetCallerIdentity`) wire contracts.

### Acceptance

1. `dotnet test --project tests/Knightfall.ScaleSet.Aws.SdkContract.Tests/Knightfall.ScaleSet.Aws.SdkContract.Tests.csproj --configuration Release` passes offline after package restore, using fake `HttpMessageHandler` transports and deterministic `TimeProvider`.
2. Any SDK discrepancy or behavioral caveat (such as AWS SDK v4 `ClientRequestToken` auto-fill behavior) is documented explicitly for T06–T17.

---

## T03: Define v1alpha1 schemas and TypeScript config compiler

**Depends on:** [T01](#t01-pin-build-tooling-roslynator-and-cdk-baseline), [T02](#t02-verify-scaleset-client-and-aws-sdk-v4-contracts).

### Deliverable

Publish closed JSON Schemas (`cdk/config/schema/controller-config-v1alpha1.json`, `controller-runtime-v1alpha1.json`), the deterministic TypeScript YAML-to-JSON compiler (`cdk/lib/config/`, `cdk/bin/emit-runtime.ts`), and shared fixtures in `testdata/config/`.

### Requirements

- Parse YAML strictly: reject duplicate keys, YAML aliases/anchors, merge keys, custom tags, non-string map keys, and multi-document files.
- Enforce `apiVersion: scaleset-aws/v1alpha1`, `kind: RunnerController`, closed objects, tagged unions, and cross-field validation:
  - `spec.controller.hosting`: `ecs-fargate` only (`network`, `cluster`, `cpuArchitecture`, `cpu`, `memoryMiB`) plus ECR `@sha256:...` digest-pinned `spec.controller.image`. Reject non-Fargate hosting variants cleanly.
  - `spec.github`: reject inline credentials, mismatched enterprise/App auth, custom GHES hosts, and `caBundlePath` (with clear deferral messages referencing `scaleset.NET` `#20`/`#21`).
  - `spec.profiles[]`: at least one `active` or `draining` profile; `0 <= idleReserve <= maxRunners` and `maxRunners > 0`; disjoint labels within each GitHub scope; scale-set name included in `labels`.
  - `provisioner` (`ec2` vs `fargate`): reject cross-variant keys; validate `ec2.launchTemplateVersion` union (`pinned` positive integer string vs `alias` `$Latest` or `$Default`) and `fargate.taskDefinitionArn` revision suffix.
- Normalize output into canonical JSON (`RunnerControllerRuntime`) whose SHA-256 digest is invariant under YAML comment edits and mapping key reordering.
- Provide shared fixtures (`ec2-only-latest.yaml`, `ec2-only-pinned.yaml`, `fargate-only.yaml`, `mixed-ec2-fargate.yaml`, `rejections.json`, and compiled `testdata/config/runtime/*.json`).

### Acceptance

1. `npm --prefix cdk test` exercises positive fixtures, key-order invariance, and every negative rejection rule in `rejections.json`.
2. `npm --prefix cdk run config:emit -- ../testdata/config/ec2-only-latest.yaml /tmp/runtime.json` prints a deterministic SHA-256 matching the committed golden output.

---

## T04: Validate runtime JSON and conditional provider factories

**Depends on:** [T03](#t03-define-v1alpha1-schemas-and-typescript-config-compiler).

### Deliverable

Implement `Knightfall.ScaleSet.Aws.Core` strict runtime JSON verification, domain model translation, and conditional provider registration spies proving unselected AWS/GitHub services are never constructed.

### Requirements

- Read `RunnerControllerRuntime` JSON via `System.Text.Json` rejecting duplicate JSON property names, unknown fields, missing required fields, and invalid SHA-256 digest mismatches against expected file/S3 bytes.
- Run the exact same semantic validation rules as TypeScript (`T03`) inside `Knightfall.ScaleSet.Aws.Core` without taking any dependency on `AWSSDK.*` or `Knightfall.ScaleSet.*`.
- Separate read-only dry-run composition from active controller composition:
  - Active EC2-only initializes Direct EC2 provisioner and controller-cluster `DescribeTasks` fencing reader only; zero Fargate runner provisioner or runner `RunTask`/`StopTask` clients.
  - Active Fargate-only initializes Fargate runner provisioner only; zero EC2 `RunInstances`/`TerminateInstances` clients.
  - Dry-run initializes zero mutators, zero Secrets Manager clients, zero SQS consumers, and zero GitHub session creators.

### Acceptance

1. Cross-language parity tests in `Knightfall.ScaleSet.Aws.Core.Tests` assert identical pass/reject outcomes across every fixture in `testdata/config/` and `testdata/config/rejections.json`.
2. Constructor-spy tests verify zero invalid client constructions across invalid config, dry-run, EC2-only, Fargate-only, and disabled-backend permutations.

---

## T05: Implement capacity planner and offline dry-run engine

**Depends on:** [T04](#t04-validate-runtime-json-and-conditional-provider-factories).

### Deliverable

Implement the provider-neutral `CapacityPlanner` and offline advisory dry-run report engine (`reconcile --dry-run --once`) in `Knightfall.ScaleSet.Aws.Core`, using `TimeProvider` and `testdata/dryrun/replay.json`.

### Requirements

- Implement exact planning math:
  - `target = min(effectiveMax, TotalAssignedJobs + effectiveIdleReserve)` where `draining` forces `effectiveIdleReserve = 0`.
  - `deficit = max(0, target - eligibleServing - crediblePending)`.
  - Preserve active/busy and uncertain capacity even when occupancy exceeds `target`; never emit blanket scale-down proposals when demand is stale (`> 10m` default freshness limit) or absent (`none`).
- Support all three dry-run demand modes (`none`, `RunnerDemandSnapshot`, `RunnerDemandReplay`), advancing virtual `TimeProvider` across ordered/disordered replay records in `testdata/dryrun/replay.json`.
- Report explicitly when `inspectAws: true` was configured but offline mode executed without live AWS inspection.
- Enforce compile-time separation so the dry-run engine cannot reference effect-dispatch or session-creation interfaces.

### Acceptance

1. Table-driven tests cover zero/positive demand, `draining` zero reserve, unknown occupancy, startup timeout expiry, backend headroom caps, stale demand, disordered replay, and orphan reporting.
2. Offline `reconcile --config ... --dry-run --once` emits deterministic advisory JSON with zero AWS or GitHub calls.
