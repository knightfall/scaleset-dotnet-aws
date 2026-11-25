# Verify a controller release

Download into a fresh directory using a GitHub CLI version that supports
`gh release verify`, `gh release verify-asset`, and `gh attestation verify`.
Select an explicit tag from
[GitHub Releases](https://github.com/knightfall/scaleset-dotnet-aws/releases).

```sh
repo=knightfall/scaleset-dotnet-aws
tag=v0.1.0-alpha.1  # Replace with the release you intend to verify.
download_dir=$(mktemp -d) || exit 1

gh release verify "$tag" --repo "$repo" || exit 1
gh release download "$tag" --repo "$repo" --dir "$download_dir" || exit 1
for asset in "$download_dir"/*; do
  gh release verify-asset "$tag" "$asset" --repo "$repo" || exit 1
done
```

GitHub verifies the release attestation and asset digests. Continue with the
build attestation and payload checks below before using the downloaded files.
[GitHub CLI release verification](https://cli.github.com/manual/gh_release_verify)

## Verify the published manifest and payloads

Resolve the release tag to its commit at the upstream GitHub repository. For an
annotated tag, use the commit it points to. Run the following from a trusted
checkout of that exact commit. The verifier reads the authenticated manifest and
checks the exact asset set and every payload digest, including the OCI archives
and their SBOMs. Complete this check before loading an archive or pushing to ECR.

```sh
commit="<verified-40-character-tag-commit>"
test "$(git rev-parse HEAD)" = "$commit" || exit 1
gh attestation verify "$download_dir/release.json" --repo "$repo" \
  --signer-workflow "$repo/.github/workflows/release-candidate.yml" \
  --source-ref refs/heads/main --source-digest "$commit" --deny-self-hosted-runners || exit 1
python3 .github/release/candidate.py verify "$download_dir" --commit "$commit" || exit 1
```

This also checks `SHA256SUMS` against the authenticated file set. You can use
that file for subsequent offline checksum checks.

### Inspect an Actions candidate

Run these commands from the reviewed checkout with the commit, run ID, and
attempt shown on the Actions run page. Record the manifest SHA-256 after the
attestation and payload checks succeed. Use that digest for publish dispatch.

```sh
repo=knightfall/scaleset-dotnet-aws
commit="<reviewed-40-character-main-commit>"
candidate_run_id=123456789  # Replace with the selected run ID.
candidate_attempt=1        # Replace with the selected run attempt.
download_dir=$(mktemp -d)

test "$(git rev-parse HEAD)" = "$commit" || exit 1
gh run download "$candidate_run_id" --repo "$repo" \
  --name "controller-candidate-$commit-$candidate_attempt" --dir "$download_dir" || exit 1
gh attestation verify "$download_dir/release.json" --repo "$repo" \
  --signer-workflow "$repo/.github/workflows/release-candidate.yml" \
  --source-ref refs/heads/main --source-digest "$commit" --deny-self-hosted-runners || exit 1
python3 .github/release/candidate.py verify "$download_dir" \
  --commit "$commit" --run-id "$candidate_run_id" --attempt "$candidate_attempt" --promotion || exit 1
candidate_manifest_sha256=$(shasum -a 256 "$download_dir/release.json" | cut -d ' ' -f 1)
printf '%s\n' "$candidate_manifest_sha256"
```

## Choose the deployment archive

`release.json` records each archive's transport SHA-256 and OCI manifest
digest. Choose `linux-x64` for ECS x86-64 or `linux-arm64` for ECS Arm.
Starting with manifest schema 3, it also records the OCI config digest and
the SPDX subject linked to that manifest and platform. The corresponding
`.spdx.json` inventories that archive. Use Docker with the
containerd image store to load the verified OCI archive. After importing the
archive into ECR, bind the confirmed ECR manifest digest in the controller
YAML. Each `.grype.json` records the architecture's scan and database evidence
at build time. Follow the [registry and runtime verification steps](../operator-runbook.md#pin-the-registry-manifest). [Docker containerd image store](https://docs.docker.com/engine/storage/containerd/)

[GitHub CLI build provenance verification](https://cli.github.com/manual/gh_attestation_verify)
