# M6: Local Integration Verification and Release Readiness (T22–T26)

## T22: Verify GitHub contracts against local Scale Set simulator

**Depends on:** [T09](M2-lifecycle-sessions-handoff.md#t09-implement-admission-ledger-delivery-processor-and-listener-supervisor), [T11](M2-lifecycle-sessions-handoff.md#t11-implement-cryptographic-one-job-jit-handoff).

### Deliverable

Build the `tests/Knightfall.ScaleSet.Aws.IntegrationTests` harness running deterministically in CI (no live cloud or GitHub credentials required) against a **loopback GitHub Scale Set Protocol Simulator** (`actions/scaleset` `716908e5` wire contract) and a single unified local AWS endpoint (`AWS_ENDPOINT_URL`) backed by **MiniStack** (`ministackorg/ministack`, HTTP `http://127.0.0.1:4566`) or **AWS Emulator** (`aws/aws-emulator`, HTTPS `https://127.0.0.1:9297` with test-harness TLS trust).

### Requirements

- Implement a stateful loopback HTTP/2-capable ASP.NET Core simulator modeling GitHub.com and `*.ghe.com` App token exchange, enterprise classic PAT (`manage_runners:enterprise`) registration-token exchange, runner groups, scale set creation/lookup, message sessions, HTTP `202` empty polls, cursor `0` and monotonic message IDs, `429`/`403` rate-limit responses with `Retry-After`, dropped HTTP responses after server-side commit, and delayed job-start races.
- Exercise against MiniStack / AWS Emulator (Secrets Manager + DynamoDB) + the simulator:
  1. App (org/repo) and classic enterprise PAT auth, `create-if-missing` single-attempt registration recovery after dropped response, and `AWSCURRENT` secret rotation validating candidate credentials without opening an overlapping acquiring session.
  2. Session exclusivity, initial stats, cursor advancement, lost `DeleteMessageAsync` / `CreateMessageSessionAsync` / `GenerateJitConfigAsync` response recovery asserting exact single-attempt wire counts.
  3. Multiple concurrent `JobAvailable` offers against unchanged `TotalAssignedJobs` with controller restart between offers, validating that conservative admission reservation stays locked unless exact request attribution is present.
  4. `draining` `0` max-capacity polling and simulated `RemoveRunnerAsync` race (dispatched-before-start job vs connected idle runner), confirming automatic connected-idle termination stays disabled by default.
  5. Process loss followed by exact-session retirement from a fresh Client, with ownership fencing, journaled IDs, and a fresh checkpoint for the replacement. Suppressed requests, lost responses, 400/404 responses, and reconstructed `Acknowledging` preserve uncertainty and block unsafe replacement or old-session queue work. Model the [released recovery boundary](../dependency-baseline.md#session-recovery-and-qualification-boundary) without treating simulator outcomes as new service guarantees.

### Acceptance

1. `dotnet test --project tests/Knightfall.ScaleSet.Aws.IntegrationTests/Knightfall.ScaleSet.Aws.IntegrationTests.csproj --configuration Release` passes `T22` deterministically offline after package restore, with zero GitHub credentials.

---

## T23: Verify Direct EC2 flows against local AWS emulation

**Depends on:** [T13](M3-direct-ec2-execution.md#t13-implement-direct-ec2-launch-and-targeted-cleanup), [T14](M3-direct-ec2-execution.md#t14-implement-direct-ec2-runner-bootstrap), [T21](M5-cdk-packaging-matrix.md#t21-verify-full-offline-cdk-and-iam-synthesis-matrix), [T22](#t22-verify-github-contracts-against-local-scale-set-simulator).

### Deliverable

Verify full Direct EC2 provisioning, Launch Template version resolution, `HELLO#`/`OFFER#`/`STATUS#` handoff, zonal failover, and targeted termination end to end against **MiniStack** or **AWS Emulator** (`AWS_ENDPOINT_URL` for EC2, IAM, STS, EventBridge/SQS, and DynamoDB), a **loopback IMDSv2 stub**, and the **GitHub Scale Set Simulator**.

### Test scenarios

- Verify both `pinned` numeric and `$Latest` / `$Default` Launch Template preflight resolution in the emulated EC2 endpoint, plus **drift freeze** when an intentionally broken revision (IMDSv1 or mismatched instance profile/role) becomes `$Latest` while running allocations preserve their recorded provenance.
- Verify `RoleId:ShortInstanceId` `aws:userid` resolution across **two simulated EC2 instances** using loopback IMDSv2 + emulated IAM/STS, confirming each instance consumes only its own `OFFER#`, peer `OFFER#` access fails, and `STATUS#` records report authenticated readiness/exit.
- Run a simulated one-job runner execution end to end, inject `InsufficientInstanceCapacity` on the first subnet/AZ via the test proxy/emulator fault seam to verify a new `ClientToken` opens in an alternate subnet/AZ, confirm `TerminateInstances` reaches `terminated`, and verify an active EC2 allocation survives controller restart/replacement.

### Acceptance

1. `T23` Direct EC2 emulated integration suite passes cleanly against MiniStack / AWS Emulator in CI without live AWS accounts or a local Docker daemon.

---

## T24: Verify Fargate flows against local AWS emulation

**Depends on:** [T16](M4-fargate-execution.md#t16-implement-standalone-fargate-runtask-launch-and-cleanup), [T17](M4-fargate-execution.md#t17-implement-fargate-runner-bootstrap), [T21](M5-cdk-packaging-matrix.md#t21-verify-full-offline-cdk-and-iam-synthesis-matrix), [T22](#t22-verify-github-contracts-against-local-scale-set-simulator).

### Deliverable

Verify standalone ECS Fargate task launch, `RoleId:ShortTaskId` handoff without `STATUS#` writes, `RunTask` replay recovery, and `STOPPED` reconciliation end to end against **MiniStack** or **AWS Emulator** (`AWS_ENDPOINT_URL` for ECS, IAM, STS, EventBridge/SQS, and DynamoDB), a **loopback ECS Task Metadata v4 stub**, and the **GitHub Scale Set Simulator**.

### Test scenarios

- Verify preflight rejection for sidecars, privileged containers, missing `taskLongArnFormat`, and `Unknown` vs `0` free-IP / verified vCPU headroom.
- Verify `RoleId:ShortTaskId` `aws:userid` derivation across **two simulated Fargate tasks** using loopback Task Metadata v4 + emulated IAM/STS, proving `HELLO#`/`OFFER#` handoff succeeds with zero `STATUS#` writes and zero plaintext JIT exposure in task overrides, env, or logs.
- Run a simulated one-job execution until task exit (`STOPPED`), inject a dropped `RunTask` response and recover via `ListTasks(StartedBy=...)` + identical replay inside the 50-minute cutoff (zero launches past expiry), and verify active runner tasks survive controller replacement.

### Acceptance

1. `T24` Fargate emulated integration suite passes cleanly against MiniStack / AWS Emulator in CI, documenting any emulator-specific control-plane stubbing gaps explicitly in `T26`.

---

## T25: Verify mixed operation, dry-run, failover, restore, and rollback locally

**Depends on:** [T19](M5-cdk-packaging-matrix.md#t19-implement-opentelemetry-tracing-metrics-and-otlp-logging), [T23](#t23-verify-direct-ec2-flows-against-local-aws-emulation), [T24](#t24-verify-fargate-flows-against-local-aws-emulation).

### Deliverable

Exercise mixed EC2 + Fargate profiles, read-only dry-run request auditing, unclean ECS controller task failover fencing, DynamoDB snapshot/restore sanitization, OTLP observability correlation, and YAML-only add/drain/`preflight retire`/rollback locally in CI.

### Evidence matrix

- **Capability independence**: Exercise EC2-only, Fargate-only, and mixed profiles using the same `dotnet publish /t:PublishContainer` configuration contract; verify degrading one backend leaves healthy profiles progressing.
- **Read-only dry-run audit**: Run `reconcile --dry-run --once` against emulated S3 versioned runtime configs/snapshots and emulated DynamoDB/EC2/ECS through a recording HTTP proxy asserting **zero** DynamoDB writes, Secrets Manager reads, GitHub session calls, SQS reads, or `RunInstances`/`RunTask`/`TerminateInstances`/`StopTask` calls.
- **Unclean ECS controller failover**: Abruptly terminate one controller worker mid-effect while simulated EC2 and Fargate jobs remain active; verify the successor stays in read-only recovery until emulated `ecs:DescribeTasks` reports the exact prior controller task ARN `STOPPED` + `preflight clock` margin passes + outstanding effects settle.
- **DynamoDB restore drill**: Export/restore table state in MiniStack / AWS Emulator DynamoDB, purge all restored `HELLO#`/`OFFER#`/untrusted `STATUS#` records before reopening bootstrap access, and reconcile post-backup operations without duplicate launches.
- **OTLP telemetry & redaction**: Verify correlated logs, traces, and bounded-cardinality metrics reach a loopback OTLP receiver across mixed EC2 + Fargate operation with zero secret leakage.
- **YAML add/drain/rollback**: Add a profile via YAML emission + emulated S3 upload, drain and verify `preflight retire` passes only after two-pass clear and closure hold, and exercise compatible rollback plus blocked stale-generation rollback.

### Acceptance

1. Full `T25` emulated matrix passes in CI with zero live AWS/GitHub dependencies and verified correlated, secret-free OTLP telemetry.

---

## T26: Prepare release readiness, operator runbook, and emulator-boundary record

**Depends on:** [T25](#t25-verify-mixed-operation-dry-run-failover-restore-and-rollback-locally).

### Deliverable

Deliver the operator deployment/rollback runbook, profile binding template, CDK apply/destroy checklist, AMI OS/IMDS isolation verification checklist, and explicit **emulator-boundary record** explaining why conservative fail-closed defaults stay enabled.

### Requirements

- Document the end-to-end deployment sequence (`config:emit` -> `dotnet publish /t:PublishContainer` ECR digest push -> CDK synth/diff -> `preflight table` / `preflight clock` -> ECS Fargate deploy -> `/readyz` & OTLP checks) and drain/`preflight retire`/rollback runbooks.
- Provide the operator checklist for aspects local emulators do not enforce at kernel/cloud level: AMI IMDSv2 firewall (`169.254.169.254` / `fd00:ec2::254`) and unprivileged runner OS user separation before enabling EC2 `STATUS#`, verified account vCPU quotas for Fargate, and Secrets Manager IAM scoping.
- Record all conservative defaults retained by design (conservative admission reservation holding in `T09a`, disabled automatic connected-idle `RemoveRunnerAsync` reclamation in `T10`/`T11`, AZ-pinned EC2 `ClientToken` failover in `T13`, 50-minute Fargate replay cutoff in `T16`) alongside explicit guidance on what target evidence is required before ever relaxing them.
- Record session-recovery evidence separately for each target and authentication scope. Carry forward the released SDK's GitHub.com repository-scope qualification; do not claim that local emulation qualifies organisation/enterprise recovery, natural token expiry, or cross-process reattachment. Document how operators retain and reconcile ambiguous acknowledgements and session retirement.

### Acceptance

1. Repository documentation cleanly closes `M1`–`M6` verification in CI and gives operators a complete, auditable deployment and rollback guide.
