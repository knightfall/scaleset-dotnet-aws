#!/usr/bin/env bash
set -euo pipefail
source .github/release/tag.sh

fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

[[ "$GITHUB_REPOSITORY" == knightfall/scaleset-dotnet-aws && "$GITHUB_REF" == refs/heads/main ]] ||
  fail 'Publication must run from upstream main.'
[[ "$CANDIDATE_RUN_ID" =~ ^[1-9][0-9]*$ ]] || fail 'Candidate run ID must be a positive integer.'
[[ "$CANDIDATE_ATTEMPT" =~ ^[1-9][0-9]*$ ]] || fail 'Candidate attempt must be a positive integer.'
[[ "$CANDIDATE_MANIFEST_SHA256" =~ ^[0-9a-f]{64}$ ]] || fail 'Supply the reviewed manifest SHA-256.'
repo="repos/$GITHUB_REPOSITORY"

require_release_tag "$RELEASE_TAG" "$GITHUB_SHA"

workflow_id=$(gh api "$repo/actions/workflows/release-candidate.yml" --jq .id)
gh api "$repo/actions/runs/$CANDIDATE_RUN_ID/attempts/$CANDIDATE_ATTEMPT" | jq -e \
  --arg repo "$GITHUB_REPOSITORY" --arg sha "$GITHUB_SHA" --argjson workflow "$workflow_id" \
  --argjson run "$CANDIDATE_RUN_ID" --argjson attempt "$CANDIDATE_ATTEMPT" '
    .id == $run and .run_attempt == $attempt and .workflow_id == $workflow and .head_repository.full_name == $repo
      and .head_branch == "main" and .head_sha == $sha
      and (.event == "push" or .event == "workflow_dispatch")
      and .status == "completed" and .conclusion == "success"
  ' > /dev/null || fail 'Select a successful upstream main candidate attempt for this exact commit.'

gh api --method GET "$repo/actions/workflows/ci.yml/runs" \
  -f branch=main -f event=push -f status=success -f head_sha="$GITHUB_SHA" |
  jq -e --arg repo "$GITHUB_REPOSITORY" --arg sha "$GITHUB_SHA" '
    any(.workflow_runs[]; .head_repository.full_name == $repo and .head_sha == $sha
      and .head_branch == "main" and .event == "push" and .conclusion == "success")
  ' > /dev/null || fail 'The exact commit needs a successful main CI push run.'

artifact_name="controller-candidate-$GITHUB_SHA-$CANDIDATE_ATTEMPT"
artifact=$(gh api --method GET "$repo/actions/runs/$CANDIDATE_RUN_ID/artifacts" -f name="$artifact_name" |
  jq -ec --arg name "$artifact_name" '
    [.artifacts[] | select(.name == $name and .expired == false)] |
    select(length == 1) | .[0] | select(.id | type == "number" and . > 0)
  ') || fail 'Expected exactly one unexpired sealed candidate artifact for the selected run attempt.'
artifact_id=$(jq -r .id <<< "$artifact")
jq -e --argjson now "$(date -u +%s)" '
  (.created_at | fromdateiso8601) as $created | $created <= $now and $now - $created <= (72 * 3600)
' <<< "$artifact" > /dev/null || fail 'The sealed artifact is outside the 72-hour promotion window.'
[[ "${REVIEWED_ARTIFACT_ID+x}" != x || "$artifact_id" == "$REVIEWED_ARTIFACT_ID" ]] ||
  fail 'Current candidate artifact identity differs from the reviewed artifact.'

printf 'artifact_id=%s\n' "$artifact_id" >> "$GITHUB_OUTPUT"
