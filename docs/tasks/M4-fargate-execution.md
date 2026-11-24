# M4: ECS Fargate Execution (T15–T17)

## T15: Implement Fargate runner preflight and read-only inventory

**Depends on:** [T04](M1-foundation-config-planner.md#t04-validate-runtime-json-and-conditional-provider-factories), [T05](M1-foundation-config-planner.md#t05-implement-capacity-planner-and-offline-dry-run-engine), [T10](M2-lifecycle-sessions-handoff.md#t10-implement-lifecycle-reconciliation-dispatch-and-profile-retirement).

### Deliverable

Implement `Knightfall.ScaleSet.Aws.Providers.Fargate` read-only preflight, headroom calculation, and paginated task inventory for each enabled `fargate` profile, strictly decoupled from controller ECS hosting and Direct EC2 provisioning.

### Requirements

- **Task definition & cluster validation**:
  - Verify canonical cluster ARN (`DescribeClusters`), `taskLongArnFormat` enabled (`ListAccountSettings`), and exact revision-pinned `taskDefinitionArn` (`DescribeTaskDefinition`).
  - Require `FARGATE` compatibility, `awsvpc` networking, approved OS/architecture platform, CPU/memory pairing, exact `TaskRoleArn` and `ExecutionRoleArn` (`GetRole`), and **exactly one** essential container (`runnerContainer`).
  - Reject task definitions with sidecars, privileged containers, Docker socket mounts, ECS Exec, non-approved entrypoints/commands, or plaintext secrets.
- **Headroom & network checks**:
  - Inspect VPC/subnet/security-group validity and free subnet IP addresses (`DescribeSubnets`, `DescribeSecurityGroups`).
  - Accept operator-verified on-demand Fargate vCPU quota headroom from environment preflight evidence (with timestamp/expiry and reserved margin) instead of unconditionally constructing Service Quotas clients. Subtract in-flight launches not yet holding attached ENIs without double-counting observed running tasks; keep stale or missing quota/IP evidence as `Unknown` limit (`0` remaining headroom remains distinct from `Unknown`).
- **Scoped task inventory**:
  - Paginate cluster-scoped `ListTasks` + batched `DescribeTasks` checking both `Tasks` and `Failures`. Support `ListTasks(StartedBy=...)` correlation for T16 recovery.
  - Encode canonical incarnation from `(longTaskArn, exactTaskDefinitionArn, allocationId)`. Account for foreign tasks in shared subnets/clusters toward physical limits without authorizing cleanup.
  - Ensure Fargate-only environments never initialize EC2 instance/launch-template launch/termination clients.

### Acceptance

1. Fixtures test wrong cluster/revision/network/role, extra sidecars, privileged task definitions, disabled `taskLongArnFormat`, `Unknown` vs `0` IP/vCPU headroom, partial `Failures` entries, and foreign task accounting.
2. Host-only ECS controller deployments (`ec2` runner profiles only) never run Fargate runner preflight.

---

## T16: Implement standalone Fargate RunTask launch and cleanup

**Depends on:** [T07](M2-lifecycle-sessions-handoff.md#t07-implement-ecs-lease-ownership-lineage-and-effect-journal), [T10](M2-lifecycle-sessions-handoff.md#t10-implement-lifecycle-reconciliation-dispatch-and-profile-retirement), [T15](#t15-implement-fargate-runner-preflight-and-read-only-inventory).

### Deliverable

Implement standalone ephemeral Fargate task provisioning (`RunTask` count-1), 50-minute conservative replay protection, EventBridge reconciliation, and safe `StopTask` cleanup.

### Requirements

- **Canonical launch request & replay cutoff**:
  - Persist exact cluster ARN, revision-pinned task definition ARN, `LaunchType = FARGATE`, canonical sorted subnet/security-group lists (`AssignPublicIp = DISABLED`), `EnableExecuteCommand = false`, nonsecret overrides, ownership tags, `StartedBy = ss-<32hex>` (`<= 128` chars), `ClientToken` (`<= 64` ASCII chars), and `earliestPossibleFirstSend` before send.
  - Set `MaxErrorRetry = 0` on `RunTask` and `StopTask` so the DynamoDB journal controls retries.
  - Enforce a conservative **50-minute** replay cutoff from `earliestPossibleFirstSend` (`< 1h` ECS token minimum) minus clock/request margin. Reissue only byte-identical parameters and `ClientToken` within that open replay window; never construct new parameters across a config generation change or replay after cutoff expiry.
- **Readback, eventual consistency, and stopping**:
  - Handle HTTP 200 responses with empty `Tasks`, partial `Failures`, or dropped responses by reconciling `DescribeTasks`, `ListTasks(StartedBy=...)`, and ordered EventBridge `ECS Task State Change` SQS events.
  - Only call `StopTask` when T10 authorizes cleanup of a verified unused, atomically revoked, or safe terminal allocation. Track `STOPPED` as **resource death** independently of GitHub job completion and runner registration cleanup.

### Acceptance

1. Tests assert count-1 `RunTask` parameters, dropped response + `ListTasks(StartedBy=...)` recovery (1 wire call), identical replay before 50-minute cutoff across lease renewal, zero launches after cutoff expiry, and immediate `STOPPED` handling before runner readiness.
2. Verify `StopTask` is blocked for active, unknown, or mismatched-revision tasks.

---

## T17: Implement Fargate runner bootstrap

**Depends on:** [T11](M2-lifecycle-sessions-handoff.md#t11-implement-cryptographic-one-job-jit-handoff), [T15](#t15-implement-fargate-runner-preflight-and-read-only-inventory).

### Deliverable

Implement Fargate mode in `Knightfall.ScaleSet.Aws.RunnerBootstrap` and document the approved non-privileged task-definition contract.

### Requirements

- **Task Metadata v4 & STS validation**:
  - Validate `ECS_CONTAINER_METADATA_URI_V4` points to a bounded local loopback/link-local metadata endpoint; read task ARN, cluster ARN, task definition family:revision, and container identity, then cross-check STS `GetCallerIdentity` `UserId` (`RoleId:ShortTaskId`).
  - Write `HELLO#<aws:userid>` and conditionally consume `OFFER#<aws:userid>` via `DeleteItem(ReturnValues=ALL_OLD)`, validating `T11` AAD bindings and expiry in memory.
- **No `STATUS#` trust & essential exit**:
  - Because workflow steps executing inside the Fargate task container can potentially reach task IAM credentials, grant **zero** `STATUS#` write permissions, zero lifecycle-row permissions, and zero GitHub admin/Secrets Manager permissions to Fargate task roles.
  - Run the pinned GitHub Actions runner for **one job** in an unprivileged non-root process with bounded writable scratch, forward signals (`SIGTERM`), flush logs cleanly, and exit the essential container upon completion so the Fargate task stops naturally.

### Acceptance

1. Fixtures test invalid metadata URI, mismatched task ARN/revision/role, duplicate startup consume failure, expired/tampered `OFFER#`, and zero plaintext JIT in environment, files, overrides, or logs.
2. End-to-end container/emulated tests verify non-privileged single-job exit and zero `STATUS#` write permissions on the Fargate runner identity (`T24` verifies the full MiniStack / AWS Emulator ECS, IAM, and DynamoDB flow alongside `T21` IAM assertions).
