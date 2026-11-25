#!/usr/bin/env bash
set -euo pipefail

directory=$1
gh attestation verify "$directory/release.json" --repo "$GITHUB_REPOSITORY" \
  --signer-workflow "$GITHUB_REPOSITORY/.github/workflows/release-candidate.yml" \
  --source-ref refs/heads/main --source-digest "$GITHUB_SHA" --deny-self-hosted-runners
python3 .github/release/candidate.py verify "$directory" --commit "$GITHUB_SHA" \
  --run-id "$CANDIDATE_RUN_ID" --attempt "$CANDIDATE_ATTEMPT" \
  --manifest-sha256 "$CANDIDATE_MANIFEST_SHA256" --promotion
