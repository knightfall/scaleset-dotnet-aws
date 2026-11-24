# M3: Direct EC2 Execution (T12–T14)

## T12: Implement Direct EC2 preflight and read-only inventory

**Depends on:** [T04](M1-foundation-config-planner.md#t04-validate-runtime-json-and-conditional-provider-factories), [T05](M1-foundation-config-planner.md#t05-implement-capacity-planner-and-offline-dry-run-engine), [T10](M2-lifecycle-sessions-handoff.md#t10-implement-lifecycle-reconciliation-dispatch-and-profile-retirement).

### Deliverable

Implement `Knightfall.ScaleSet.Aws.Providers.Ec2` read-only preflight, Launch Template version resolution (supporting both `pinned` numeric versions and safely validated `$Latest` / `$Default` aliases), and paginated owned EC2 inventory.

### Requirements

- **Launch Template & version resolution**:
  - Call `DescribeLaunchTemplateVersions` for the configured `launchTemplateId` using either the pinned numeric version string or `$Latest` / `$Default`.
  - Resolve `$Latest` or `$Default` to its **exact positive numeric version** (e.g., `12`) before preflight passes. Never pass `$Latest` or `$Default` directly into `RunInstances`.
  - Validate on the resolved numeric version: approved OS/architecture via `DescribeImages`, IMDSv2 enforced (`MetadataOptions.HttpTokens == Required`), encrypted root EBS volume, matching `instanceProfileArn` -> `GetInstanceProfile` -> `GetRole` equal to `bootstrapRoleArn`, valid private subnet/VPC/security-group alignment (`DescribeSubnets`, `DescribeSecurityGroups`), and expected bootstrap tag/protocol contract.
  - If a newer `$Latest` revision fails any invariant, freeze new admissions and launches for that profile while keeping existing running allocations alive under their own recorded numeric-version provenance.
- **Owned inventory discovery**:
  - Paginate `DescribeInstances` filtered to controller/deployment/profile ownership tags and canonical resource IDs (`arn:<partition>:ec2:<region>:<account>:instance/<instance-id>`).
  - Encode canonical incarnation from `(account, region, instanceId, launchTime, resolvedLaunchTemplateVersion)` so reboots/status changes cannot forge a new lifetime, while missing/partial pages stay `Unknown`.
  - Classify `pending`/`running`/`shutting-down` separately from irreversible `terminated`. Never treat `shutting-down` or missing describe pages as terminal cleanup proof.
  - Expose identical read-only snapshots to dry-run (`inspectAws: true`) without requiring `ec2:RunInstances` or `ec2:TerminateInstances`.

### Acceptance

1. Fixtures in `Knightfall.ScaleSet.Aws.Providers.Tests` verify `pinned` numeric rejection of missing/unencrypted/IMDSv1 templates, `$Latest` numeric resolution and drift freeze when a broken version becomes `$Latest`, wrong `GetInstanceProfile`/`GetRole` mapping, incomplete pagination, and zero ECS runner API calls.
2. Empty compatible Direct EC2 inventory returns known zero occupancy distinct from unavailable/failed preflight.

---

## T13: Implement Direct EC2 launch and targeted cleanup

**Depends on:** [T07](M2-lifecycle-sessions-handoff.md#t07-implement-ecs-lease-ownership-lineage-and-effect-journal), [T10](M2-lifecycle-sessions-handoff.md#t10-implement-lifecycle-reconciliation-dispatch-and-profile-retirement), [T12](#t12-implement-direct-ec2-preflight-and-read-only-inventory).

### Deliverable

Implement idempotent count-1 Direct EC2 instance provisioning (`RunInstances`), zonal AZ failover, EventBridge/SQS reconciliation, and targeted `TerminateInstances` cleanup.

### Requirements

- **Immutable launch intent & zonal idempotency**:
  - Select one healthy private subnet/AZ from the profile's `network` (deterministic round-robin / failure-weighted selection) and resolve the current preflight-verified numeric Launch Template version before committing the `OP#` intent.
  - Persist `(subnetId, launchTemplateId, resolvedNumericVersion, instanceType, securityGroupIds, instanceProfileArn, ownershipTags, clientToken, earliestPossibleFirstSend)` in the nonsecret effect envelope before sending.
  - Issue `ec2:RunInstances` (`MinCount=1, MaxCount=1`, `ClientToken`, explicit `LaunchTemplate.Version = resolvedNumericVersion`, `SubnetId`, `TagSpecifications` on `instance` and `volume`) with `MaxErrorRetry = 0`.
  - **Zonal idempotency rule**: Never reuse a `ClientToken` across a different subnet/AZ. If `RunInstances` returns definitive `InsufficientInstanceCapacity` (or zonal placement failure) for that pinned attempt, settle that intent as definitively rejected and open a fresh intent with a new `ClientToken` targeting another eligible subnet/AZ.
- **Ambiguous write recovery & EventBridge**:
  - On lost `RunInstances` response, query `DescribeInstances` by ownership/allocation tags and `ClientToken` (within the documented EC2 token validity window) before declaring failure or launching a replacement.
  - Consume `EC2 Instance State-change Notification` SQS messages to accelerate state tracking, always converging via paginated `DescribeInstances`.
- **Targeted termination**:
  - Only after T10 grants cleanup authority and rechecks lease, incarnation, and current `OP#` state, call `ec2:TerminateInstances` (`MaxErrorRetry = 0`) for that exact `instanceId`.
  - Wait for `DescribeInstances` / EventBridge confirmation of `terminated` before marking the resource terminal and retiring GitHub state.

### Acceptance

1. Tests cover count-1 `RunInstances` pinned to resolved numeric version (never `$Latest`), dropped response after server commit + tag readback recovery (1 wire call), `InsufficientInstanceCapacity` triggering a new `ClientToken` in an alternate subnet/AZ, EventBridge out-of-order events, and exact `TerminateInstances` refusing unknown/active instances.
2. Verify zero Auto Scaling (`Amazon.AutoScaling`) dependencies or calls exist in `Knightfall.ScaleSet.Aws.Providers.Ec2`.

---

## T14: Implement Direct EC2 runner bootstrap

**Depends on:** [T11](M2-lifecycle-sessions-handoff.md#t11-implement-cryptographic-one-job-jit-handoff), [T12](#t12-implement-direct-ec2-preflight-and-read-only-inventory).

### Deliverable

Implement Direct EC2 mode in `Knightfall.ScaleSet.Aws.RunnerBootstrap` and document the AMI/OS bootstrap contract separating the supervisor identity from the one-job runner execution boundary.

### Requirements

- **Identity handshake & fail-closed consume**:
  - Read IMDSv2 instance identity document + STS `GetCallerIdentity` to verify account, Region, `instanceId`, and `UserId` (`RoleId:ShortInstanceId`) before writing `HELLO#<aws:userid>`.
  - Enforce a supervisor-owned local spent marker as defense in depth alongside the authoritative DynamoDB allocation fence so an instance reboot never requests or consumes a second JIT offer.
  - Consume `OFFER#<aws:userid>` via conditional `DeleteItem(ReturnValues=ALL_OLD)`, verify `T11` AAD bindings and expiry, and decrypt the JIT config in memory only.
- **OS & IMDS isolation before granting `STATUS#`**:
  - Run the bootstrap supervisor under a dedicated privileged service identity and spawn the pinned GitHub Actions runner process for **one ephemeral job** under an unprivileged OS identity.
  - Prove on supported Linux (and Windows if enabled) that job/workflow code — including host-level steps and local Docker jobs on `ec2-general` — cannot inspect the supervisor's process arguments/environment, cannot access IMDSv2 (`169.254.169.254` / IPv6 `fd00:ec2::254` blocked for job UIDs/containers), and cannot assume the bootstrap IAM role to forge `STATUS#<aws:userid>`.
  - Flush sanitized logs and publish authenticated readiness/exit evidence to `STATUS#<aws:userid>` only after the isolation boundary is verified; never invoke `ec2:RunInstances`, `ec2:TerminateInstances`, or Secrets Manager APIs from bootstrap.

### Acceptance

1. Fixtures test wrong instance/role/account metadata, reboot after spent marker, expired/tampered offers, lost consume response failing closed, and zero plaintext JIT in disk files, args, or logs.
2. `T23` emulated verification (MiniStack / AWS Emulator + loopback IMDSv2) proves the instance handshake, `STATUS#` flow, and cross-instance `OFFER#` isolation contract; operator AMI deployment follows the `T26` OS/IMDS isolation checklist before enabling active `STATUS#` writes.
