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
