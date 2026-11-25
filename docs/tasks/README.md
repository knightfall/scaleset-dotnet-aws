# Controller Task Specifications

The baseline merged in [PR #1](https://github.com/knightfall/scaleset-dotnet-aws/pull/1). All 26 tasks and six milestones are complete. The [task issues](https://github.com/knightfall/scaleset-dotnet-aws/issues?q=is%3Aissue+is%3Aclosed) hold the merged PRs and verification evidence. The [release process](../releases/PUBLISHING.md) covers controller distribution.

These milestone documents specify all 26 implementation and verification tasks from the [implementation plan](../implementation-plan.md) for the `.NET 10` AWS runner scale set controller. The original delivery plan includes the concurrency, lease-fencing, bounded-recovery, and conservative-admission fixes learned from the [Go reference review](https://github.com/knightfall/scaleset-example-aws/blob/cefd50b24a5fd965af1b803a659bcc64e17c40cd/docs/review-triage.md) and the `D1`–`D9` invariants below from day one.

## Task index

The [OpenTelemetry integration plan](../opentelemetry-integration.md) expands T19's signal/configuration contracts and maps host wiring, synthesis, mixed-profile verification, and operator guidance to T20, T21, T25, and T26.

| Task | Specification | Milestone |
| --- | --- | --- |
| T01 | [Pin build tooling, Roslynator, and CDK baseline](M1-foundation-config-planner.md#t01-pin-build-tooling-roslynator-and-cdk-baseline) | M1: Foundation, configuration, and planner |
| T02 | [Verify ScaleSet Client and AWS SDK v4 contracts](M1-foundation-config-planner.md#t02-verify-scaleset-client-and-aws-sdk-v4-contracts) | M1: Foundation, configuration, and planner |
| T03 | [Define v1alpha1 schemas and TypeScript config compiler](M1-foundation-config-planner.md#t03-define-v1alpha1-schemas-and-typescript-config-compiler) | M1: Foundation, configuration, and planner |
| T04 | [Validate runtime JSON and conditional provider factories](M1-foundation-config-planner.md#t04-validate-runtime-json-and-conditional-provider-factories) | M1: Foundation, configuration, and planner |
| T05 | [Implement capacity planner and offline dry-run engine](M1-foundation-config-planner.md#t05-implement-capacity-planner-and-offline-dry-run-engine) | M1: Foundation, configuration, and planner |
| T06 | [Implement DynamoDB lifecycle store and bounded active-op index](M2-lifecycle-sessions-handoff.md#t06-implement-dynamodb-lifecycle-store-and-bounded-active-op-index) | M2: Lifecycle, sessions, and handoff |
| T07 | [Implement ECS lease ownership, lineage, and effect journal](M2-lifecycle-sessions-handoff.md#t07-implement-ecs-lease-ownership-lineage-and-effect-journal) | M2: Lifecycle, sessions, and handoff |
| T08 | [Add Secrets Manager GitHub App and PAT loading and scale-set resolution](M2-lifecycle-sessions-handoff.md#t08-add-secrets-manager-github-app-and-pat-loading-and-scale-set-resolution) | M2: Lifecycle, sessions, and handoff |
| T09 | [Implement admission ledger, delivery processor, and listener supervisor](M2-lifecycle-sessions-handoff.md#t09-implement-admission-ledger-delivery-processor-and-listener-supervisor) | M2: Lifecycle, sessions, and handoff |
| T10 | [Implement lifecycle reconciliation, dispatch, and profile retirement](M2-lifecycle-sessions-handoff.md#t10-implement-lifecycle-reconciliation-dispatch-and-profile-retirement) | M2: Lifecycle, sessions, and handoff |
| T11 | [Implement cryptographic one-job JIT handoff](M2-lifecycle-sessions-handoff.md#t11-implement-cryptographic-one-job-jit-handoff) | M2: Lifecycle, sessions, and handoff |
| T12 | [Implement Direct EC2 preflight and read-only inventory](M3-direct-ec2-execution.md#t12-implement-direct-ec2-preflight-and-read-only-inventory) | M3: Direct EC2 execution |
| T13 | [Implement Direct EC2 launch and targeted cleanup](M3-direct-ec2-execution.md#t13-implement-direct-ec2-launch-and-targeted-cleanup) | M3: Direct EC2 execution |
| T14 | [Implement Direct EC2 runner bootstrap](M3-direct-ec2-execution.md#t14-implement-direct-ec2-runner-bootstrap) | M3: Direct EC2 execution |
| T15 | [Implement Fargate runner preflight and read-only inventory](M4-fargate-execution.md#t15-implement-fargate-runner-preflight-and-read-only-inventory) | M4: ECS Fargate execution |
| T16 | [Implement standalone Fargate RunTask launch and cleanup](M4-fargate-execution.md#t16-implement-standalone-fargate-runtask-launch-and-cleanup) | M4: ECS Fargate execution |
| T17 | [Implement Fargate runner bootstrap](M4-fargate-execution.md#t17-implement-fargate-runner-bootstrap) | M4: ECS Fargate execution |
| T18 | [Compose shared TypeScript CDK infrastructure and IAM](M5-cdk-packaging-matrix.md#t18-compose-shared-typescript-cdk-infrastructure-and-iam) | M5: CDK, observability, packaging, and synthesis matrix |
| T19 | [Implement OpenTelemetry tracing, metrics, and OTLP logging](M5-cdk-packaging-matrix.md#t19-implement-opentelemetry-tracing-metrics-and-otlp-logging) | M5: CDK, observability, packaging, and synthesis matrix |
| T20 | [Build controller runtime, OCI container, and ECS Fargate host](M5-cdk-packaging-matrix.md#t20-build-controller-runtime-oci-container-and-ecs-fargate-host) | M5: CDK, observability, packaging, and synthesis matrix |
| T21 | [Verify full offline CDK and IAM synthesis matrix](M5-cdk-packaging-matrix.md#t21-verify-full-offline-cdk-and-iam-synthesis-matrix) | M5: CDK, observability, packaging, and synthesis matrix |
| T22 | [Verify GitHub contracts against local Scale Set simulator](M6-local-integration-readiness.md#t22-verify-github-contracts-against-local-scale-set-simulator) | M6: Local integration verification and release readiness |
| T23 | [Verify Direct EC2 flows against local AWS emulation](M6-local-integration-readiness.md#t23-verify-direct-ec2-flows-against-local-aws-emulation) | M6: Local integration verification and release readiness |
| T24 | [Verify Fargate flows against local AWS emulation](M6-local-integration-readiness.md#t24-verify-fargate-flows-against-local-aws-emulation) | M6: Local integration verification and release readiness |
| T25 | [Verify mixed operation, dry-run, failover, restore, and rollback locally](M6-local-integration-readiness.md#t25-verify-mixed-operation-dry-run-failover-restore-and-rollback-locally) | M6: Local integration verification and release readiness |
| T26 | [Prepare release readiness, operator runbook, and emulator-boundary record](M6-local-integration-readiness.md#t26-prepare-release-readiness-operator-runbook-and-emulator-boundary-record) | M6: Local integration verification and release readiness |

## Original implementation sequence

1. **Core foundation (`T01` -> `T05`)**: Lock `.NET 10` / Roslynator / CDK tooling, confirm `Knightfall.ScaleSet.Client` and AWS SDK v4 contracts, emit canonical `v1alpha1` artifacts, and validate runtime parity + offline planner/dry-run.
2. **Durable state & control plane (`T06` -> `T11`)**: Build the DynamoDB table store with active-op discovery from the start (`T06`), lease/epoch lineage and canonical effect envelopes (`T07`), Secrets Manager App/PAT loading (`T08`), admission/delivery/listener supervision (`T09`), provider-neutral reconciliation/dispatch/drain (`T10`), and ECDH P-256/AES-GCM one-job JIT handoff (`T11`).
3. **Parallel provider tracks (`M3` & `M4`)**: Once `T10d` and `T11` define the dispatch, inventory, and handoff contracts, Direct EC2 (`T12`–`T14`) and ECS Fargate (`T15`–`T17`) can develop in parallel.
4. **Infrastructure, observability & hosting (`M5`)**: `T18` consumes `T10e` retirement gates and `T11` IAM contracts; `T19` instruments libraries and wires the Serilog/OpenTelemetry OTLP pipeline + `telemetry-smoke`; `T20` wires the root `.NET 10` CLI, native OCI image (`dotnet publish /t:PublishContainer`), and 1-replica ECS Fargate service; `T21` runs the full offline synthesis matrix.
5. **Emulated verification & release readiness (`M6`)**: `T22` runs against the loopback GitHub Scale Set simulator + MiniStack (`AWS_ENDPOINT_URL`) once `T09` and `T11` land, followed by end-to-end MiniStack verification (`T23`–`T25`) and operator runbook/boundary documentation (`T26`).

## Cross-repo `scaleset.NET` dependencies

- **Released Client and Listener for T01/T02/T09**: Pin both packages to exact
  `0.1.0-alpha.1` from the published `v0.1.0-alpha.1` release, source commit
  `a0bc2060715767bff0f1a22a9f328689ed363493`. The
  [dependency baseline](../dependency-baseline.md) records the manifest and
  archive hashes, registry verification, extension contracts, and recovery scope.
  T02 verifies the public package boundary; T09 implements the DynamoDB
  checkpoint adapter, replay-safe handler, operation policy, and serialized
  supervisor lane around `ScaleSetListenerPump.StepAsync`.
- **Completed session-retirement dependency**: Upstream
  [#64](https://github.com/knightfall/scaleset.NET/issues/64) qualified confirmed
  retirement of the exact journaled session for GitHub.com repository scope.
  T09 uses the released `DeleteMessageSessionAsync` after ownership fencing.
  Ambiguous acknowledgements/retirement retain their evidence and block the lane;
  new sessions get fresh checkpoint identity. Cross-process reattachment, natural
  token expiry, and broader target/authentication scopes remain unqualified.
  Keep those boundaries in T22 simulator cases and T26 operator guidance.
- **Deferred upstream issues**: Custom GHES hosts (`scaleset.NET` `#20`) and custom root CA / mTLS transport factories (`scaleset.NET` `#21`) remain out of scope for v1 and are rejected cleanly during config and startup validation.

## GitHub issues tracking convention

- Create six GitHub Milestones (`M1` through `M6`) and 26 lightweight Issues (`T01` through `T26`) in `scaleset-dotnet-aws` after the planning-baseline PR is merged. T01 depends on that merged baseline; all other blockers follow the task dependency graph.
- Keep specification prose in `docs/`. Each issue links to its `M*.md#tXX` section at the merged baseline commit, lists issue dependencies (`Blocked by #...`), and records implementation PRs and verification evidence. Update the specification in a PR when requirements change, then update the issue's specification link.
- Title issues `TXX: <task title>` and assign the matching M1–M6 milestone. Use `enhancement` for implementation, `documentation` for operator guidance, focused `area:*` labels, and `priority:P1`/`priority:P2` according to implementation order. Mark an issue blocked while prerequisite work is incomplete.
- Use `Closes #...` in the completing PR, not merely in a planning issue. Close a task only when its deliverable is merged and its acceptance evidence is recorded; close a milestone when all its tasks meet that condition.
- The baseline PR publishes specifications only. Creating issues, implementing tasks, and cloud deployment are subsequent work.

## Baked-in architectural invariants (D1–D9)

| Commitment | Design rule baked in from T01 | Automated & emulated proof and conservative boundary |
| --- | --- | --- |
| D1: Bounded discovery | `T06`/`T10c` build `ACTIVE-OP#<gen>#<intent>`, transactional `EFFECTS` count, writer-protocol continuity, and checkpointed `EFFECT-REBUILD` (32 rows/page, 2 pages/pass) in the initial store; `due` GSI is hint-only. | Test interrupted rebuild across 10,000 retained rows and empty/lagging GSI against MiniStack DynamoDB without starving profiles. |
| D2: Dispatch and admission | `T09`/`T10d`/`T13`/`T16` use immutable nonsecret request envelopes, revision-checked begin transitions, and exact `OutsideAssigned` subtraction (cap-10 regression). Never release reservations on aggregate assigned counts alone. | Test cap-10 regression, concurrent `PersistDelivery`/`ObserveResource`, definitive rejection, and readback ambiguity; conservative retention stays enabled by default after `T22` simulator checks. |
| D3: Handoff and exit | `T11` uses `RoleId:ShortId` `aws:userid`, `ECDH P-256 + HKDF-SHA256 + AES-256-GCM`, and competing consume/revoke. `T14` isolates EC2 OS/IMDS before granting `STATUS#`; `T17` denies Fargate `STATUS#` writes. | Prove cross-principal `OFFER#` logic and `STATUS#` boundary in `T23`/`T24` with MiniStack + CDK IAM policy assertions (`T21`) + operator AMI checklist (`T26`). |
| D4: GitHub semantics | `T22` tests App/PAT auth, assignment-to-statistics ordering races, drain `0` capacity, session loss/404 handling, and `RemoveRunnerAsync` fence behavior in the local GitHub Scale Set simulator. | Keep conservative admission accounting and disabled automatic connected-idle termination by default unless explicitly opted into for a verified target. |
| D5: AWS contracts | `T12`/`T13` validate `pinned` and `$Latest`/`$Default` numeric resolution, zonal `ClientToken`, and `MaxErrorRetry = 0`. `T15`/`T16` validate single-container Fargate, free-IP + verified vCPU headroom, and 50-minute replay cutoff. | Prove Direct EC2 LT/zonal failover (`T23`) and ECS `RunTask` replay/headroom (`T24`) against MiniStack (`AWS_ENDPOINT_URL`) while keeping zonal token & quota rules strict. |
| D6: ECS host fencing | `T07`/`T20` bind every lease incarnation to its exact controller ECS task ARN (`ownerTaskArn`). Successor remains in read-only recovery until direct ECS `STOPPED` evidence, or a qualified captured terminal event for an exact `MISSING` task, establishes predecessor termination alongside the clock margin and settled effects. Exact GitHub session retirement remains independent. [Capture qualification](../controller-predecessor-capture.md). | Prove unclean controller ECS task replacement against emulated `ecs:DescribeTasks` and clock-skew boundary fixtures in `T25`. |
| D7: Startup and packaging | `T18`–`T21` pin S3 config digest, `--generation`, native OCI image digest (`PublishContainer`), loopback `/healthz`/`/readyz`, OTLP configuration (`T19`), and 60s SIGTERM inside 90s ECS `stopTimeout`. | Exercise built OCI image and strict offline synth matrix; verify S3 config/version checks against MiniStack S3 in `T25`. |
| D8: Drain closure | `T10e`/`T18` require two separated clear passes, durable session closure hold, strong base-table rechecks, tombstone, and runtime `preflight retire` before CDK drops IAM/rules. | Test restart/rotation during closure hold, late adverse observations, and `preflight retire` against MiniStack DynamoDB in `T25`. |
| D9: Immutable bindings | `T10a`/`T26` keep GitHub set/group/label/bootstrap-role/LT-ID bindings immutable in v1 (`launchTemplateVersion: alias` tracks resolved numeric provenance per allocation). | Exercise additive migration and block accidental claim reuse across profiles. |

## Cross-task acceptance

| Requirement | Implementation evidence | Emulated integration evidence (`T22`–`T25`) |
| --- | --- | --- |
| Independent EC2-only and Fargate-only runner profiles under ECS controller hosting | `T04`, `T18`, `T20`, `T21` | `T23`, `T24`, `T25` |
| Safe Direct EC2 `$Latest`/`$Default` numeric resolution and drift freeze | `T03`, `T12`, `T13` | `T23` |
| Correlated OTLP logs, traces, and bounded-cardinality metrics without secret leakage | `T19`, `T20`, `T21` | `T25` |
| YAML-only profile addition and verified drain/retirement | `T10`, `T18`, `T21` | `T25` |
| No duplicate provision, registration, or unsafe cleanup across restart/failover | `T06`–`T11`, `T13`, `T16` | `T22`, `T23`, `T24`, `T25` |
| Enterprise PAT and App secrets never reach runner workloads | `T08`, `T11`, `T14`, `T17`, `T18` | `T22`, `T23`, `T24` |
| Read-only dry-run without AWS/GitHub mutation | `T05`, `T12`, `T15`, `T18`, `T20` | `T25` |
| Active runners survive controller ECS task replacement and rollback | `T07`, `T10e`, `T13`, `T16`, `T20` | `T23`–`T26` |
