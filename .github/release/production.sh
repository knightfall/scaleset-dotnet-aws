#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

repo="repos/$GITHUB_REPOSITORY"
[[ -n "${RELEASE_SETTINGS_READ_TOKEN:-}" ]] || fail 'Configure RELEASE_SETTINGS_READ_TOKEN in production.'
settings_token=$RELEASE_SETTINGS_READ_TOKEN
unset RELEASE_SETTINGS_READ_TOKEN

settings_read() {
  GH_TOKEN="$settings_token" gh api "$@"
}

settings_read "$repo/immutable-releases" | jq -e '.enabled == true' > /dev/null ||
  fail 'Enable immutable releases and verify that RELEASE_SETTINGS_READ_TOKEN can read repository administration settings.'
settings_read "$repo" | jq -e '.default_branch == "main"' > /dev/null ||
  fail 'Release protections require main to be the repository default branch.'

ruleset_ids=$(settings_read --paginate --slurp "$repo/rulesets?includes_parents=true&targets=branch,tag&per_page=100" |
  jq -er --arg repo "$GITHUB_REPOSITORY" '
    [.[][] | select(.enforcement == "active" and .source_type == "Repository" and .source == $repo) | .id] |
    select(length > 0 and all(.[]; type == "number" and . > 0 and . == floor)) | unique[]
  ') || fail 'Could not discover active repository branch and tag rulesets.'
rulesets=$(mktemp)
trap 'rm -f "$rulesets"' EXIT
while IFS= read -r id; do
  settings_read "$repo/rulesets/$id" >> "$rulesets" || fail 'Could not read an active repository ruleset.'
done <<< "$ruleset_ids"
unset settings_token
jq -se --arg repo "$GITHUB_REPOSITORY" -f .github/release/production-rules.jq "$rulesets" > /dev/null ||
  fail 'Required main and release-tag rulesets or explicit bypass evidence are missing. Check rule targeting, enforcement and settings token visibility.'

gh api "$repo/environments/production" | jq -e '
  .can_admins_bypass == false and .deployment_branch_policy.custom_branch_policies == true
  and any(.protection_rules[]; .type == "required_reviewers" and (.reviewers | length > 0))
' > /dev/null || fail 'Production must have required reviewers, no administrator bypass and a custom deployment branch policy.'
gh api "$repo/environments/production/deployment-branch-policies" | jq -e '
  .total_count == 1 and .branch_policies[0].name == "main" and .branch_policies[0].type == "branch"
' > /dev/null || fail 'Production must permit only the main branch.'
