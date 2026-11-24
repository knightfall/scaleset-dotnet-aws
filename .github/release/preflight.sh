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
repo="repos/$GITHUB_REPOSITORY"

[[ -n "${RELEASE_SETTINGS_READ_TOKEN:-}" ]] || fail 'Configure RELEASE_SETTINGS_READ_TOKEN in production.'
GH_TOKEN="$RELEASE_SETTINGS_READ_TOKEN" gh api "$repo/immutable-releases" --jq .enabled | grep -qx true ||
  fail 'Enable immutable releases and verify that RELEASE_SETTINGS_READ_TOKEN can read repository administration settings.'
unset RELEASE_SETTINGS_READ_TOKEN
gh api "$repo/environments/production" | jq -e '
  .can_admins_bypass == false and .deployment_branch_policy.custom_branch_policies == true
  and any(.protection_rules[]; .type == "required_reviewers" and (.reviewers | length > 0))
' > /dev/null || fail 'Production must have required reviewers, no administrator bypass and a custom deployment branch policy.'
gh api "$repo/environments/production/deployment-branch-policies" | jq -e '
  .total_count == 1 and .branch_policies[0].name == "main" and .branch_policies[0].type == "branch"
' > /dev/null || fail 'Production must permit only the main branch.'

require_release_tag "$RELEASE_TAG" "$GITHUB_SHA"

workflow_id=$(gh api "$repo/actions/workflows/release-candidate.yml" --jq .id)
attempt=$(gh api "$repo/actions/runs/$CANDIDATE_RUN_ID" | jq -er \
  --arg repo "$GITHUB_REPOSITORY" --arg sha "$GITHUB_SHA" --argjson workflow "$workflow_id" '
    select(.workflow_id == $workflow and .head_repository.full_name == $repo
      and .head_branch == "main" and .head_sha == $sha
      and (.event == "push" or .event == "workflow_dispatch")
      and .status == "completed" and .conclusion == "success")
    | .run_attempt | select(type == "number" and . > 0)
  ') || fail 'Select a successful upstream main release candidate for this exact commit.'

gh api --method GET "$repo/actions/workflows/ci.yml/runs" \
  -f branch=main -f event=push -f status=success -f head_sha="$GITHUB_SHA" |
  jq -e --arg repo "$GITHUB_REPOSITORY" --arg sha "$GITHUB_SHA" '
    any(.workflow_runs[]; .head_repository.full_name == $repo and .head_sha == $sha
      and .head_branch == "main" and .event == "push" and .conclusion == "success")
  ' > /dev/null || fail 'The exact commit needs a successful main CI push run.'

artifact_name="controller-candidate-$GITHUB_SHA-$attempt"
artifact_id=$(gh api --method GET "$repo/actions/runs/$CANDIDATE_RUN_ID/artifacts" -f name="$artifact_name" |
  jq -er --arg name "$artifact_name" '
    [.artifacts[] | select(.name == $name and .expired == false)] |
    select(length == 1) | .[0].id | select(type == "number" and . > 0)
  ') || fail 'Expected exactly one unexpired sealed candidate artifact for the selected run attempt.'

printf 'artifact_id=%s\nattempt=%s\n' "$artifact_id" "$attempt" >> "$GITHUB_OUTPUT"
