# Publish a controller release

The release process distributes the controller as two Linux OCI archives through
[GitHub Releases](https://github.com/knightfall/scaleset-dotnet-aws/releases).
Each architecture has an SPDX image SBOM. The release also includes the license,
a source-bound manifest, and SHA-256 checksums. Use the
[deployment runbook](../operator-runbook.md) to import the selected image into
the operator's ECR repository and qualify it for deployment.

GitHub Actions uses OIDC to sign the build provenance for `release.json`.
The protected publish job uploads the verified OCI archives with its short-lived
`GITHUB_TOKEN` to GitHub Releases. Operators authenticate separately to their
own ECR registry when importing an archive. [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations) · [GitHub Actions job token](https://docs.github.com/en/actions/concepts/security/github_token)

## Configure protections once

- Require squash-merged pull requests, resolved review threads, and successful
  CI for `main`. Protect `refs/tags/v*` against updates and deletion and restrict
  creation to repository administrators. Enable immutable releases in the
  repository settings.
- Create a `production` environment. Select the deployment branch policy
  **Selected branches and tags**, with a single branch rule for `main`.
  Configure required reviewers and disable administrator bypass. Where the
  maintainer team supports it, prevent self-review.
- Add the `production` environment secret `RELEASE_SETTINGS_READ_TOKEN`.
  Use a fine-grained token scoped to this repository with **Administration**
  read access and an expiration. The workflow uses it for the immutable
  releases API, which requires that permission. Candidate retrieval,
  attestation verification, and publication use the job's `GITHUB_TOKEN`.

[GitHub deployment environments](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments) · [GitHub immutable releases](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes) · [Immutable releases API permissions](https://docs.github.com/en/rest/repos/repos#check-if-immutable-releases-are-enabled-for-a-repository)

## Select a verified candidate

1. Select a commit on `main` whose [CI push run](https://github.com/knightfall/scaleset-dotnet-aws/actions/workflows/ci.yml) passed. Review the changes and qualification notes. Choose a `vMAJOR.MINOR.PATCH` tag, with an optional prerelease suffix such as `-alpha.1`.
2. Select the successful [Controller release candidate](https://github.com/knightfall/scaleset-dotnet-aws/actions/workflows/release-candidate.yml) run at that same commit. The workflow builds on native GitHub-hosted amd64 and arm64 runners, verifies each OCI archive against its tested assembly, smoke-tests each image on a read-only filesystem with networking disabled, and inventories the tested images with Syft. The sealing job records the exact commit, run ID and attempt in `release.json`, attests that manifest, and uploads the combined artifact for 30 days.
3. [Download and verify the candidate](VERIFYING.md#inspect-an-actions-candidate) to inspect its manifest and payloads before approving. If the build needs a retry, rerun all candidate jobs or dispatch a new candidate from the same main head. The sealed artifact binds both architecture builds to one run attempt.
4. Create the release tag at the selected commit with the authorized release identity. Check the tag, CI run, and candidate run before dispatch. Dispatch from `main` while it points to that commit.

```sh
repo=knightfall/scaleset-dotnet-aws
tag=v0.1.0-alpha.1  # Example version. Select the actual approved release tag.
candidate_run_id=123456789  # Replace with the selected main candidate run ID.

gh workflow run release.yml --repo "$repo" --ref main \
  -f "tag=$tag" -f "candidate_run_id=$candidate_run_id"
```

## Approve publication

Review the dispatch commit, tag and candidate ID in the workflow run, then
approve the `production` job. The job checks the exact remote tag, successful
main CI and candidate run, selects the unexpired artifact by ID, verifies its
provenance and checks every payload against the attested manifest. It publishes
those same bytes to the existing tag. A tag with a prerelease suffix produces
a GitHub prerelease.

The workflow only accepts a tag at the dispatch commit. Review and dispatch
again if main advanced before dispatch. Use a new version to supersede a
published immutable release. If publication stops with an incomplete draft,
inspect its assets and tag before removing that draft and retrying the same
candidate. A candidate that has expired requires a new candidate run. Record
the successful publication run, exact commit and candidate run in the release
evidence. [Release creation behavior](https://cli.github.com/manual/gh_release_create)

After publication, verify the public release and its assets from a fresh
directory. Qualify live AWS, GitHub and OTLP targets through the
[deployment checks](../emulator-boundaries.md#pending-target-checks).
