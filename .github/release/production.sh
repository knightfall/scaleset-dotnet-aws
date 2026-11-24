#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

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
