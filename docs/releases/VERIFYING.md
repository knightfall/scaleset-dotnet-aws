# Verify a controller release

Download into a fresh directory using a GitHub CLI version that supports
`gh release verify`, `gh release verify-asset`, and `gh attestation verify`.
Select an explicit tag from
[GitHub Releases](https://github.com/knightfall/scaleset-dotnet-aws/releases).

```sh
repo=knightfall/scaleset-dotnet-aws
tag=v0.1.0-alpha.1  # Replace with the release you intend to verify.
download_dir=$(mktemp -d)

gh release verify "$tag" --repo "$repo"
gh release download "$tag" --repo "$repo" --dir "$download_dir"
for asset in "$download_dir"/*; do
  gh release verify-asset "$tag" "$asset" --repo "$repo" || exit 1
done
(cd "$download_dir" && shasum -a 256 -c SHA256SUMS)
```

GitHub verifies the release attestation and asset digests. The checksum file also
supports subsequent offline checks once the release assets have been
authenticated. [GitHub CLI release verification](https://cli.github.com/manual/gh_release_verify)

## Verify the candidate provenance

Resolve the release tag to its commit in the remote repository or a trusted
checkout. For an annotated tag, use the commit it points to. Then verify the
build manifest against that commit.

```sh
commit="<expected-40-character-commit>"
gh attestation verify "$download_dir/release.json" --repo "$repo" \
  --signer-workflow "$repo/.github/workflows/release-candidate.yml" \
  --source-ref refs/heads/main --source-digest "$commit" --deny-self-hosted-runners
```

### Inspect an Actions candidate

Run these commands from the reviewed checkout with the commit, run ID, and
attempt shown on the Actions run page. The build attestation is checked before
the local verifier reads the payloads.

```sh
repo=knightfall/scaleset-dotnet-aws
commit="<reviewed-40-character-main-commit>"
candidate_run_id=123456789  # Replace with the selected run ID.
candidate_attempt=1        # Replace with the selected run attempt.
download_dir=$(mktemp -d)

gh run download "$candidate_run_id" --repo "$repo" \
  --name "controller-candidate-$commit-$candidate_attempt" --dir "$download_dir" || exit 1
gh attestation verify "$download_dir/release.json" --repo "$repo" \
  --signer-workflow "$repo/.github/workflows/release-candidate.yml" \
  --source-ref refs/heads/main --source-digest "$commit" --deny-self-hosted-runners || exit 1
python3 .github/release/candidate.py verify "$download_dir" \
  --commit "$commit" --run-id "$candidate_run_id" --attempt "$candidate_attempt"
```

## Choose the deployment archive

`release.json` records each archive's transport SHA-256 and OCI manifest
digest. Choose `linux-x64` for ECS x86-64 or `linux-arm64` for ECS Arm.
The corresponding `.spdx.json` inventories that image. After importing the
archive into ECR, bind the confirmed ECR manifest digest in the controller
YAML. Follow the [registry and runtime verification steps](../operator-runbook.md#pin-the-registry-manifest).

[GitHub CLI build provenance verification](https://cli.github.com/manual/gh_attestation_verify)
