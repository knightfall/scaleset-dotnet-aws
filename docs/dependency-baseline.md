# Released scaleset.NET dependency baseline

Verified on 24 November 2026. This is the upstream SDK baseline for T01, T02,
and T09; it does not claim that the AWS controller is implemented or qualified.

## Release identity

| Item | Required baseline |
| --- | --- |
| Target framework | `.NET 10` (`net10.0`) |
| NuGet packages | `Knightfall.ScaleSet.Client` and `Knightfall.ScaleSet.Listener` |
| Package version | Exact `0.1.0-alpha.1` for both packages |
| Release tag | [`v0.1.0-alpha.1`](https://github.com/knightfall/scaleset.NET/releases/tag/v0.1.0-alpha.1) |
| Release source commit | [`a0bc2060715767bff0f1a22a9f328689ed363493`](https://github.com/knightfall/scaleset.NET/commit/a0bc2060715767bff0f1a22a9f328689ed363493) |
| Release manifest | [`release.json`](https://github.com/knightfall/scaleset.NET/releases/download/v0.1.0-alpha.1/release.json) |
| Manifest SHA-256 | `ccf97b15a0c4f85490b28e12a4e770303f5e74a3e4c63f101ea0f8397eb981eb` |

The manifest records these SHA-256 digests for the original GitHub release
archives:

| Archive | SHA-256 |
| --- | --- |
| `Knightfall.ScaleSet.Client.0.1.0-alpha.1.nupkg` | `943d1acf4a2076d7b884e406ac3d576f9982027e1d34f3624692c1e1388b83c0` |
| `Knightfall.ScaleSet.Listener.0.1.0-alpha.1.nupkg` | `01b8d686419a951c7b9b58918f8eaf56e39ee63e57502e594093dd76f8203066` |

T01 consumes the published packages from NuGet.org with exact central versions,
committed NuGet lock files, and locked CI restore. Do not use the earlier local
candidate feed or a sibling project reference: candidate builds reused the same
preview version. T02 verifies consumption in an isolated package cache and
records the resolved package identities. Follow the upstream
[artifact verification procedure](https://github.com/knightfall/scaleset.NET/blob/v0.1.0-alpha.1/docs/releases/PUBLISHING.md#verify-release-artifacts)
for provenance, SBOM, and registry signatures. NuGet.org adds a repository
signature, so its archive bytes need not match the original release archive
hashes above. A package upgrade requires an explicit baseline change and rerun
of the relevant SDK and integration contracts.

Use the release's [central dependency versions](https://github.com/knightfall/scaleset.NET/blob/v0.1.0-alpha.1/Directory.Packages.props)
when selecting compatible direct dependencies in T01. In particular, do not
force all `Microsoft.Extensions.*` packages to `10.0.12`: the released SDK uses
`Microsoft.Extensions.Logging.Abstractions` `11.0.0`. Keep the AWS controller
independent of `Knightfall.ScaleSet.Hosting` and `Knightfall.ScaleSet.Providers.Docker`.

## Listener integration contract

T09 composes `ScaleSet.Listener.ScaleSetListenerPump` through the GitHub adapter
with these released interfaces:

- `IScaleSetListenerCheckpointStore`: consistent reads and atomic writes that
  enforce the expected revision and current ownership fence.
- `IScaleSetListenerDeliveryHandler`: current admission calculation and
  replay-safe effects before returning `Completed`, or explicit `Deferred`.
- `IScaleSetListenerExecutionPolicy`: authorization before each controlled
  operation, fixed ownership cancellation, finite deadlines, and poll capacity.

Keep the exact scope/set/session key, schema, transition ID, revision, original
observation time, nullable confirmed cursor, and pending phase intact. A normal
real delivery uses four logical checkpoint writes. `Received` resumes handling;
`Processed` resumes acknowledgement; uncertain `Acknowledging` returns
`RecoveryRequired`. DynamoDB storage and the business-effect ledger still own
distributed fencing and deduplication. See the
[released Listener contract](https://github.com/knightfall/scaleset.NET/blob/v0.1.0-alpha.1/docs/product/LISTENER-EXTENSIBILITY.md)
and [package consumer example](https://github.com/knightfall/scaleset.NET/tree/v0.1.0-alpha.1/examples/checkpointed-listener).

## Session recovery and qualification boundary

| Situation | Required controller behaviour |
| --- | --- |
| Same session client is still open | Await the previous step and reconstruct the pump with that client and the exact key. Resume the stored phase before polling. |
| Previous process/client is lost | After fencing the previous owner, journal retirement and call `ScaleSetClient.DeleteMessageSessionAsync(scaleSetId, sessionId, token)` with administration credentials for the configured target. Successful completion confirms retirement of that exact session. |
| Retirement succeeds | Reconcile and retain old unresolved delivery/reservation evidence, then create an independently authorized replacement with a new session key, initial statistics, and absent cursor. |
| Acknowledgement or retirement is ambiguous | Preserve uncertainty and stop the affected lane pending reconciliation or independent retirement evidence. A 400/404 or local elapsed time is not sufficient proof. Never carry the old cursor or DELETE to a replacement session. |

Upstream [#58](https://github.com/knightfall/scaleset.NET/issues/58) and
[#64](https://github.com/knightfall/scaleset.NET/issues/64) are completed for
their recorded scopes. #64 includes live GitHub.com repository-scope tests
using a personal-account credential and local Docker runners. Those observations
do not establish cross-process reattachment, natural token-expiry recovery,
or recovery guarantees for other target/authentication scopes. T09 can now
implement confirmed retirement; T22 and T26 retain target-specific qualification
boundaries and conservative handling of ambiguous outcomes. A local simulator
does not expand the live service qualification.

Custom GHES hosts ([#20](https://github.com/knightfall/scaleset.NET/issues/20)) and
custom root CA/mTLS helpers ([#21](https://github.com/knightfall/scaleset.NET/issues/21))
remain outside this baseline and must be rejected during configuration validation.

## T02 contract verification

### Published package identity

An isolated restore from NuGet.org resolved Client and Listener
`0.1.0-alpha.1`. Both registry archives passed `dotnet nuget verify --all`
with the NuGet.org repository signature. Each archive had the same 10 release
entries as its GitHub counterpart and one additional signature entry. The
SHA-512 digests of the original archives matched the NuGet content hashes in
both test project lock files. The original archives also matched the manifest's
SHA-256 digests and passed both SLSA provenance and SPDX SBOM verification
against `publish.yaml` at the release source commit.

The contract suite builds a separate [Listener consumer fixture](../tests/Knightfall.ScaleSet.Aws.ListenerConsumer/).
Its dependency graph contains the pinned Client and Listener packages, their
transitive dependencies, and the repository analyzer. It implements the
checkpoint store, delivery handler, and execution policy through the public
package interfaces.

### GitHub adapter and session supervision (T08–T11)

- The released SDK exposes the GitHub.com and GitHub Enterprise Cloud target
  shapes. Enterprise credentials must use the PAT path. The service determines
  the token's granted scopes. The controller rejects custom GHES hosts and
  `caBundlePath` during configuration validation.
- The create and update requests serialize default label types as `System`.
  Lookup responses can explicitly return a missing scale set or runner.
- Use the sanitized exception diagnostics when recording GitHub errors.
  `ScaleSetApiException.Response` retains raw response data. Preserve status
  and rate-limit metadata in controller error records and exclude the raw
  response, credential material, and session queue URLs from telemetry.
- An administrative `DeleteMessageSessionAsync` sends a single DELETE for
  the exact scale-set ID and session GUID. A dropped response or a 400/404
  remains uncertain under the controller's recovery policy. The session
  client's queue acknowledgement can refresh credentials on recognized
  token expiry. The client adopts the credentials and retries only if the
  refresh returns the same session GUID. A reconstructed `Acknowledging`
  checkpoint after a lost response stops the pump for recovery under the
  exact session identity.

### AWS adapters and journal-owned retries (T06–T17)

- The pinned DynamoDB, EC2, and ECS marshallers generate a UUID when their
  idempotency token is omitted. The generated value stays in the wire payload.
  Assign the controller's token before the journaled call and reuse that exact
  token for an eligible replay of the same immutable request. Keep each new
  transaction or launch intent's token distinct.
- Configure journal-owned AWS write clients with both `MaxErrorRetry = 0`
  and `MaxStaleConnectionRetries = 0`. The pinned SDK handles stale pooled
  connection resets under a separate retry budget. These operations expose
  the retry limits on the client configuration, so use a dedicated configured
  client for journal-owned writes. The fake transport checks count SDK sends
  at the HTTP handler boundary. Recover uncertain service outcomes through the
  effect journal.
- DynamoDB transaction cancellation reasons follow the request's item order.
  The SDK can return an absent reason list. Treat missing reasons as unknown.
  The conditional consume path requests `ALL_OLD` and extracts the preimage
  from the successful `DeleteItem` response.
- EC2 Describe responses and ECS `ListTasks` can return continuation
  tokens. ECS `DescribeTasks` takes a batch of task IDs and returns separate
  task and failure lists. Inspect those lists independently on `DescribeTasks`
  and `RunTask`, including HTTP 200 responses with partial failures.

Run the [offline contract suite](../tests/Knightfall.ScaleSet.Aws.SdkContract.Tests/)
after updating either package family. The suite instantiates the pinned SDKs
with fake HTTP handlers. AWS emulator and live target qualification use the
separate evidence recorded by later tasks.
