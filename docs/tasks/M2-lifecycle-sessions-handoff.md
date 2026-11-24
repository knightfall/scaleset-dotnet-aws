# M2: Lifecycle, Sessions, and Secure Handoff (T06–T11)

## T06: Implement DynamoDB lifecycle store and bounded active-op index

**Depends on:** [T04](M1-foundation-config-planner.md#t04-validate-runtime-json-and-conditional-provider-factories).

### Deliverable

Implement `Knightfall.ScaleSet.Aws.DynamoDb` single-table storage with revision-checked transactions, strongly consistent reads, active-only discovery (`ACTIVE-OP#`), transactional unfinished-effect counting (`EFFECTS`), checkpointed bounded rebuild (`EFFECT-REBUILD`), `due` GSI wake-up hints, and selective TTL.

### Requirements

- Enforce the exact single-table key layout from [Section 6](../implementation-plan.md#6-dynamodb-recovery-and-orphan-policy), keeping unique item keys per DynamoDB `TransactWriteItems` call (checking transaction item-count headroom for lease condition + `OP#` + `ACTIVE-OP#` + `EFFECTS`).
- Set explicit per-transaction `ClientRequestToken` when verified necessary in [T02](M1-foundation-config-planner.md#t02-verify-scaleset-client-and-aws-sdk-v4-contracts).
- Maintain `ACTIVE-OP#<index-gen>#<intent>` markers and `EFFECTS` count atomically in the same transaction when an effect becomes unfinished and delete the marker when it settles.
- Implement `EFFECT-REBUILD` reading strongly consistent `OP#` pages (maximum 32 source rows per page, maximum 2 pages per pass) while keeping profile journal writes closed until the final fenced transaction publishes completion and the verified count.
- Keep `due` `KEYS_ONLY` GSI (`dueShard` across 8 shards / `dueAtMillis`) strictly as an eventually consistent wake-up hint; recheck strongly from base-table keys before acting.
- Apply `expiresAt` numeric TTL strictly to settled `MSG#`, `JOB#`, and settled `ACQUIRE#` history after the configured retention window; never apply TTL to `LEASE`, `PROFILE#`, `CLAIM#`, `SESSION`, `CHECKPOINT#`, `DEMAND`, `ADMISSION`, `EFFECTS`, `EFFECT-REBUILD`, `ACTIVE-OP#`, unresolved `OP#`, nonterminal `RUNNER#`, or active handoffs. Ensure physically present expired rows never trigger re-execution or cleanup.

### Acceptance

1. Tests cover transaction contention, interrupted `EFFECT-REBUILD` over 10,000 retained `OP#` rows across two profiles with unequal backlogs, empty/lagging `due` GSI, and physically present expired TTL rows.
2. Steady-state restart recovery reads zero settled `OP#` rows once active index continuity is valid.

---

## T07: Implement ECS lease ownership, lineage, and effect journal

**Depends on:** [T02](M1-foundation-config-planner.md#t02-verify-scaleset-client-and-aws-sdk-v4-contracts), [T06](#t06-implement-dynamodb-lifecycle-store-and-bounded-active-op-index).

### Deliverable

Implement conditional deployment lease acquisition, heartbeat renewal, writer-protocol lineage, ECS controller task fencing, and immutable nonsecret canonical effect envelopes.

### Requirements

- Bind every controller process incarnation to a unique lease owner (`ownerId`, monotonic `epoch`, `ownerTaskArn`, `previousOwnerTaskArn`, `writerProtocol`, `validUntil`).
- On takeover after unclean loss, hold the lease in **recovery-only** mode until:
  1. `ecs:DescribeTasks` on the controller cluster proves the exact `previousOwnerTaskArn` reached `STOPPED` (or verified dual-surface isolation when unavailable).
  2. Clock-skew validation confirms bounded host drift relative to write deadlines and lease margins.
  3. Any `EFFECT-REBUILD` completes and all ambiguous `executing`/`uncertain` effects resolve.
- Reject reuse of an existing `EFFECT-REBUILD` index shortcut unless writer-protocol lineage across the previous lease proves continuity without unindexed mutations.
- Persist an explicitly versioned, canonical **nonsecret** request payload, its SHA-256 digest, initiating generation, exact resource/incarnation target, earliest possible first-send timestamp, attempt deadline, and separate optional provider replay deadline before any external send.
- Distinguish provably unstarted cancellation, confirmed application, definitive rejection tied to this exact attempt, and confirmed absence after deadline. Keep `Knightfall.ScaleSet.Aws.DynamoDb` free of GitHub credentials or plaintext JIT.

### Acceptance

1. Tests cover clean lease handoff, stale-epoch transaction rejection, suspended former controller takeover blocked until prior controller task `STOPPED` + clock-margin proof, and non-reconstructable replay digests across config generations.
2. Tests verify attempt deadline renewal never extends an expired provider replay deadline.

---

## T08: Add Secrets Manager GitHub App and PAT loading and scale-set resolution

**Depends on:** [T02](M1-foundation-config-planner.md#t02-verify-scaleset-client-and-aws-sdk-v4-contracts), [T04](M1-foundation-config-planner.md#t04-validate-runtime-json-and-conditional-provider-factories), [T07](#t07-implement-ecs-lease-ownership-lineage-and-effect-journal).

### Deliverable

Implement `Knightfall.ScaleSet.Aws.GitHub` Secrets Manager credential loading, candidate validation/rotation, and lease-fenced scale-set lookup/creation via `Knightfall.ScaleSet.Client`.

### Requirements

- Load `AWSCURRENT` secret versions from `Amazon.SecretsManager`:
  - `github-app`: JSON `{ clientId, installationId, privateKey }` for GitHub.com or `*.ghe.com` org/repo targets.
  - `enterprise-pat`: JSON `{ token }` classic PAT (`manage_runners:enterprise`) for `https://<host>/enterprises/<slug>` targets.
- Validate candidate rotated secret versions by calling `GetRunnerGroupByNameAsync` and `FindScaleSetAsync` / `GetScaleSetAsync` on the resolved group, scale-set name, optional pinned `scaleSetId`, and labels **without** opening a second message session. Keep the still-valid current credential active if candidate validation fails.
- Support `registration: existing` and `create-if-missing` (active mode only, unpinned `scaleSetId`, lease-fenced effect committed before sending one single-attempt `ScaleSetRequestRetry` `CreateScaleSetAsync` call, confirmed by lookup on dropped response).
- Sanitize all exceptions (`ScaleSetApiException` / `ScaleSetRateLimitException`), preserving HTTP status and rate-limit metadata while stripping PEMs, PATs, and session URLs.
- Guarantee dry-run mode never resolves Secrets Manager values or exchanges GitHub tokens.

### Acceptance

1. Fixtures test App/PAT parsing, invalid JSON/scope rejection, `create-if-missing` lost response + lookup recovery (asserting 1 wire request), and candidate validation keeping current credentials alive on rejection.
2. Redaction tests scan exception strings and structured logs for synthetic secrets and confirm zero dry-run secret calls.

---

## T09: Implement admission ledger, delivery processor, and listener supervisor

**Depends on:** [T05](M1-foundation-config-planner.md#t05-implement-capacity-planner-and-offline-dry-run-engine), [T06](#t06-implement-dynamodb-lifecycle-store-and-bounded-active-op-index), [T07](#t07-implement-ecs-lease-ownership-lineage-and-effect-journal), [T08](#t08-add-secrets-manager-github-app-and-pat-loading-and-scale-set-resolution).

### Deliverable

Deliver durable admission accounting, replay-safe delivery/acknowledgement handling, multi-profile session supervision, and integration with the extensible `Knightfall.ScaleSet.Listener` seam.

### T09a: Durable admission reservations

- Reserve distinct offered request IDs atomically across `ADMISSION` and `ACQUIRE#<request>` before calling `AcquireJobsAsync`; only the winner of that lease-fenced transaction transmits the batch.
- Keep both accepted and uncertain requests reserved across polls, restarts, and replays outside the generic `due` index.
- Release reservations **only** on exact terminal/rejection evidence or verified request-specific assignment evidence (simulated in `T22`; conservative double-reservation mode remains enabled by default unless target-specific ordering is explicitly enabled).

### T09b: Delivery processing and ack sequencing

- Persist sanitized `MSG#<session>#<id>` inbox receipts, monotonic job observations, and current-session demand before acknowledging via `DeleteMessageAsync`.
- Never advance cursor on lost ack response; preserve prior non-stale statistics timestamp on empty polls; never overwrite demand to zero on missing samples or convert duplicate deliveries into demand increments.
- Distinguish `0` message IDs, null/empty `202` polls, and unacknowledged pending-delivery replay across restarts.

### T09c: Session ownership, rotation, and listener extensibility seam

- Journal `SESSION` open/close intents before calling `CreateMessageSessionAsync`, `CloseAsync`, or `DeleteMessageSessionAsync`. Scope checkpoints and deliveries to the exact `(GitHub host/target scope, scaleSetId, sessionId)` key.
- Integrate `ScaleSetListenerPump.StepAsync` through a narrow T09 adapter.
  Consume the published Client and Listener `0.1.0-alpha.1` packages under the
  [released dependency baseline](../dependency-baseline.md). Implement
  `IScaleSetListenerCheckpointStore`, `IScaleSetListenerDeliveryHandler`, and
  `IScaleSetListenerExecutionPolicy` in the GitHub adapter; translate persistence
  through the DynamoDB adapter without introducing SDK types into Core.
- Store each session's logical checkpoint under `CHECKPOINT#<session>` within
  the immutable profile binding. Preserve schema, revision, transition ID,
  original observation time, nullable cursor, and pending payload/phase on round
  trip. Initial statistics can be pending without a queue message ID. Empty polls
  are not durable deliveries. Keep old session checkpoints when `SESSION` moves
  to a replacement; do not overwrite the unresolved history.
- The checkpoint store checks revision and the profile's ownership condition in
  the same DynamoDB transaction. Budget four checkpoint transactions per completed
  real delivery, plus acquisition ledger and reconciliation transactions.
  `EFFECT-REBUILD` may defer processing. Recompute capacity in the handler
  after poll and during replay, then reserve before any acquisition.
- Bind each pump to the current ownership incarnation. Journal closure and
  replacement after the active step settles. A successful close of a still-open
  client retires that session. Retain unresolved old checkpoints and reservations
  and give a replacement session its own cursor and initial statistics.
- When the previous process is gone, establish ownership and fence the old
  controller under T07, then journal and call `DeleteMessageSessionAsync` with
  the exact saved IDs and target. Confirmed retirement enables replacement
  only after the supervisor's admission/effect checks. A lost response or an
  expired/missing-session error retains quarantine pending independent evidence.
  [Upstream #64](https://github.com/knightfall/scaleset.NET/issues/64) is completed
  for its GitHub.com repository scope; wider scopes and cross-process reattachment
  remain outside that qualification. Do not turn the observed same-session DELETE
  retry behaviour into an automatic retry policy for uncertain acknowledgements.
- Supervise one serialized lane per enabled profile; share `ScaleSetRateLimitException` cooldown state across profiles referencing the same GitHub credential secret ARN; hand over sessions at clean boundaries on `AWSCURRENT` rotation.

### Acceptance

1. Tests cover consecutive `JobAvailable` offers against unchanged `TotalAssignedJobs` with a restart in between, lost `AcquireJobsAsync` / `DeleteMessageAsync` responses, concurrent `401`/rate-limit cooldown sharing across two profiles, and graceful shutdown closing only owned sessions without cancelling acks using the host stop token.
2. Checkpoint adapter tests cover exact transition readback after a lost write response, revision/ownership conflicts, preserved observation timestamps, and all pending phases. Known DELETE success permits settlement-only retry; reconstructed `Acknowledging` returns `RecoveryRequired` with no new poll or handler invocation.
3. Fresh-client retirement tests distinguish confirmed retirement from a lost response, 400/404, or stale ownership. Only the confirmed path may create a replacement; the replacement starts with initial statistics and no old cursor while unresolved old records remain retained.

---

## T10: Implement lifecycle reconciliation, dispatch, and profile retirement

**Depends on:** [T05](M1-foundation-config-planner.md#t05-implement-capacity-planner-and-offline-dry-run-engine), [T07](#t07-implement-ecs-lease-ownership-lineage-and-effect-journal), [T09](#t09-implement-admission-ledger-delivery-processor-and-listener-supervisor).

### Deliverable

Deliver provider-neutral profile reconciliation, conservative capacity projection, revision-checked effect dispatch, and verified profile drain/retirement in `Knightfall.ScaleSet.Aws.Core` + `Knightfall.ScaleSet.Aws.DynamoDb`.

### T10a: Rollout catalog, immutable bindings, and tombstones

- Persist applied configuration `--generation`, runtime digest, immutable profile bindings (`scopeURL`, `runnerGroup`, `scaleSet`, `scaleSetId`, `labels`, `launchTemplateId`/`taskDefinitionArn`, `bootstrapRoleArn`, `bootstrapProtocol`), and retired tombstones.
- Reject stale config generations and accidental binding reuse across profiles; require additive new profiles or explicit handoff for identity transfer.

### T10b: Executor ledger, classifier, and profile inventory

- Retain AWS resource observations and authenticated runner-process observations orthogonally with local timestamps and sanitized evidence links. Preserve original `startedAt` across refreshes; keep irreversible termination and one-job exit terminal while moving late contradictory observations into `Recovering`.
- Classify serving capacity (requires fresh resource presence), completed cleanup candidates (exact job completion + matched process exit + positive log-flush grace), and unknown occupancy (e.g. exited process with unresolved job).

### T10c: Bounded active-work discovery and immutable effect envelopes

- Integrate T06/T07 active-op discovery, writer continuity checks, and immutable nonsecret effect envelopes into per-profile reconciliation loops so recovery advances without scanning settled history.

### T10d: Revision-checked dispatch and conservative admission projection

- Reread lease, catalog, unfinished effects, allocation, and fresh provider preconditions before committing `planned -> executing` with revision preconditions.
- Project verified state into listener capacity (`Maximum`, `OutsideAssigned`), explicitly passing the **cap-10 regression test** (cap 10, 8 assigned including 2 matched busy + 2 unknown executors -> `OutsideAssigned = 2`, 0 new acquisitions allowed).
- Test simultaneous `PersistDelivery` and `ObserveResource` updates on one allocation with deterministic optimistic retry and bounded failure handling.

### T10e: Shared root ownership, two-pass drain, and retirement closure

- Coordinate lease renewal, graceful shutdown (`StopAdmissions -> CancelPolls -> JoinMutators -> CancelUnstartedEffects -> CloseOwnedSessions -> VerifyEffects -> ReleaseLease`), and `draining` progression: zero reserve -> two separated authoritative clear passes -> durable session closure hold -> strong base-table recheck -> `PROFILE#<id>` tombstone.

### Failure and restart matrix

| Fault | Required outcome |
| --- | --- |
| Crash before intent / before durable begin | Zero external sends; next owner plans cleanly or safely cancels/rearms unstarted intent. |
| Begin committed, DynamoDB response lost | Treat as potentially dispatched; require provider readback before rearming or freeing capacity. |
| Provider accepted, response dropped | Preserve `executing`/`uncertain` effect and stable target; rediscover via provider adapter; zero competing writes. |
| Stale lease or expired attempt | Cooperatively cancel worker token and fail DynamoDB epoch condition. |
| Fresh adverse job/process fact before dispatch | Abort cleanup candidate and preserve active/conflicted executor. |

### Acceptance

1. Fake-provider suites verify every row of the failure matrix, the cap-10 admission test, concurrent delivery/observation updates, 10,000-row bounded recovery, and full `draining` closure hold through restart/rotation.

---

## T11: Implement cryptographic one-job JIT handoff

**Depends on:** [T07](#t07-implement-ecs-lease-ownership-lineage-and-effect-journal), [T08](#t08-add-secrets-manager-github-app-and-pat-loading-and-scale-set-resolution), [T10](#t10-implement-lifecycle-reconciliation-dispatch-and-profile-retirement).

### Deliverable

Implement `Knightfall.ScaleSet.Aws.Handoff` and controller/table integration issuing a single encrypted, expiring one-job JIT offer to an authenticated EC2 instance or Fargate task incarnation.

### T11a: SDK and item-scoped handoff storage boundaries

- Wire single-attempt `ScaleSetRequestRetry` on `GenerateJitConfigAsync`, plus exact lookup (`FindRunnerByNameAsync`, `GetRunnerAsync`) and gated `RemoveRunnerAsync` (disabled for connected idle runners by default; scheduling-fence races are exercised in the `T22` simulator).
- Add explicit `HELLO#<aws:userid>`, `OFFER#<aws:userid>`, and `STATUS#<aws:userid>` table operations under `dynamodb:LeadingKeys` constraints without loosening general lifecycle partition rules.

### T11b: Cryptographic envelope and atomic publish/consume/revoke

- Derive expected `aws:userid` from `GetInstanceProfile`/`GetRole` (`RoleId:ShortInstanceId` for EC2; `RoleId:ShortTaskId` for Fargate) paired with controller-observed AWS resource identity — **never** trust caller-supplied resource or role IDs from `HELLO#`.
- Persist deterministic runner name and fenced JIT intent before calling `GenerateJitConfigAsync`; on lost response, preserve uncertainty and verify by lookup without minting a second runner.
- Encrypt using `.NET 10` BCL cryptography (`System.Security.Cryptography`): fresh ephemeral **ECDH P-256** (`ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)`), **HKDF-SHA256** (`HKDF.DeriveKey`), and **AES-256-GCM** (`AesGcm`) with 12-byte cryptographic nonce, strict public-key validation, and canonical AAD binding protocol version, deployment, profile, scale set, allocation, canonical resource ARN, incarnation, `aws:userid`, boot nonce, deterministic offer ID, and expiry.
- Publish `OFFER#` and durable allocation release state atomically. Bootstrap consumes via conditional `DeleteItem(ReturnValues=ALL_OLD)`; controller revokes unconsumed offers via competing conditional transaction. Never re-mint on lost consume receipt.
- Restrict `STATUS#<aws:userid>` strictly to EC2 bootstrap (`T14`); Fargate receives zero `STATUS#` write permissions.

### Acceptance

1. Cryptographic vector tests cover ECDH P-256 + HKDF-SHA256 + AES-GCM seal/open, wrong recipient key, tampered AAD/ciphertext, all-zero/invalid curve points, and expired offer rejection.
2. Fault tests prove 1 wire `GenerateJitConfigAsync` attempt on dropped response, duplicate/spoofed `HELLO#` rejection, concurrent consume/revoke race (single winner), and zero plaintext JIT in DynamoDB rows or logs.
