# Publish a controller release

The release process distributes the controller as two Linux OCI archives through
[GitHub Releases](https://github.com/knightfall/scaleset-dotnet-aws/releases).
Each architecture has an SPDX image SBOM and a Grype advisory report. The
release also includes the license, a source-bound manifest, and SHA-256
checksums. Use the
[deployment runbook](../operator-runbook.md) to import the selected image into
the operator's ECR repository and qualify it for deployment.

GitHub Actions uses OIDC to sign the build provenance for `release.json`.
The protected publish job uploads the verified OCI archives with its short-lived
`GITHUB_TOKEN` to GitHub Releases. Operators authenticate separately to their
own ECR registry when importing an archive. [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations) · [GitHub Actions job token](https://docs.github.com/en/actions/concepts/security/github_token)

## Configure protections once

- In active repository rulesets, protect `main` from deletion and non-fast-forward updates.
  Require squash-merged pull requests, resolved review threads, and the strict
  `check` status from GitHub Actions. Keep the bypass list empty. Protect all
  `refs/tags/v*` against updates and deletion with an empty bypass list. Require
  a separate creation rule with repository administrators as its only bypass.
  Enable immutable releases in the repository settings.
- Create a `production` environment. Select the deployment branch policy
  **Selected branches and tags**, with a single branch rule for `main`.
  Configure required reviewers and disable administrator bypass. Where the
  maintainer team supports it, prevent self-review.
- Add the `production` environment secret `RELEASE_SETTINGS_READ_TOKEN`.
  Use a fine-grained token from an administrator, scoped to this repository
  with **Administration** read access, the required **Metadata** read access,
  and an expiration. The workflow uses it for the immutable-release setting,
  default branch and active repository rulesets. Candidate retrieval,
  attestation verification, and publication use the job's `GITHUB_TOKEN`.

Before creating the tag, qualify the **exact token** intended for production.
Run the read-only check in Bash from the reviewed checkout when provisioning
the token, before storing it as the environment secret. The check uses the same
ruleset predicate as the protected job for main, tag immutability and tag
creation. Save only the pass or fail result. The protected job independently
checks the current rulesets again before publishing.

```bash
read -r -s -p 'Release settings token: ' release_settings_token
printf '\n'
GITHUB_REPOSITORY=knightfall/scaleset-dotnet-aws \
  RELEASE_SETTINGS_READ_TOKEN="$release_settings_token" \
  bash .github/release/production.sh --settings-only
unset release_settings_token
```

GitHub can omit `bypass_actors` based on a caller's ruleset access. Another
credential with the same issuer and scopes cannot establish visibility for
the intended token. Hold the tag and publication if the token has expired,
visibility cannot be established, or the ruleset evidence is incomplete.
Bring the failure to the maintainer with the sanitized check result.

[GitHub deployment environments](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments) · [GitHub immutable releases](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes) · [Immutable releases API permissions](https://docs.github.com/en/rest/repos/repos#check-if-immutable-releases-are-enabled-for-a-repository) · [Ruleset detail and bypass visibility](https://docs.github.com/en/rest/repos/rules#get-a-repository-ruleset)

## Select a verified candidate

Every push to `main` builds both native architectures. A manual dispatch on `main` can refresh candidate evidence. Pull requests into `main` trigger the two native jobs for controller source, dependency and build configuration, embedded schemas, configuration and Core test fixtures, and the image/release helpers. A documentation-only PR does not request these builds. The PR jobs select and verify the event's exact head repository and commit with read-only checkout. A fork may need maintainer approval to run. PR artifacts are review evidence. The sealing job runs only for the canonical repository on `refs/heads/main` outside pull requests. Local fork fixtures verify source selection and refusal of a different head. Qualifying GitHub's hosted fork token, approval, and both runner pools remains a separate check. [GitHub pull request events and fork restrictions](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#pull_request)

1. Select a commit on `main` whose [CI push run](https://github.com/knightfall/scaleset-dotnet-aws/actions/workflows/ci.yml) passed. Review the changes and qualification notes. Choose a `vMAJOR.MINOR.PATCH` tag, with an optional prerelease suffix such as `-alpha.1`.
2. Select the successful [Controller release candidate](https://github.com/knightfall/scaleset-dotnet-aws/actions/workflows/release-candidate.yml) run at that same commit. Native GitHub-hosted amd64 and arm64 jobs verify and smoke-test the OCI archives, scan them with Grype, and generate SPDX inventories from the same archives with Syft. The jobs verify that the evidence identifies each archive's actual OCI manifest and platform. An unavailable advisory database, a failed scan, or a High or Critical match blocks sealing. The sealing job records image, inventory and scan identities, commit, run ID, and attempt in `release.json`, attests the manifest, and uploads the combined artifact for 30 days. Review the [image advisory record](IMAGE-SECURITY.md) when the base or scan results change.
   Both native jobs verify the Docker client and daemon versions against the Engine version pinned in the workflow before building.
3. [Download and verify the candidate](VERIFYING.md#inspect-an-actions-candidate). Record its run ID, attempt and verified manifest SHA-256 for dispatch. At inspection and again after approval, the sealed artifact and both Grype database builds must be at most 72 hours old. The 30-day artifact retention is for inspection and evidence. If the build needs a retry or evidence ages out, rerun all candidate jobs or dispatch a new candidate from the same main head and review the new attempt.
4. Confirm that the exact production settings token passed the read-only check and remains valid. Create the release tag at the selected commit with the authorized release identity. Check the tag, CI run, and candidate run before dispatch. Dispatch from `main` while it points to that commit.

```sh
repo=knightfall/scaleset-dotnet-aws
tag=v0.1.0-alpha.2  # Example version. Select the actual approved release tag.
candidate_run_id=123456789  # Replace with the selected main candidate run ID.
candidate_attempt=1        # Replace with the inspected run attempt.
candidate_manifest_sha256="<sha256-from-the-inspected-manifest>"

gh workflow run release.yml --repo "$repo" --ref main \
  -f "tag=$tag" -f "candidate_run_id=$candidate_run_id" \
  -f "candidate_attempt=$candidate_attempt" -f "candidate_manifest_sha256=$candidate_manifest_sha256"
```

## Approve publication

The verification job checks the tag, exact main CI and candidate attempt,
build provenance, payload digests and freshness before `production` approval. Compare
its run summary with the recorded commit, tag, candidate run, attempt, artifact
ID and manifest digest. The protected job rechecks the exact attempt and
artifact ID, downloads that artifact, rechecks the approved manifest SHA-256
and every payload, and verifies the current branch, tag and production protections
before publishing the bytes to the existing tag. A tag with a prerelease suffix produces a GitHub
prerelease.

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
