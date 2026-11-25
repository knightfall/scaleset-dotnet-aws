# Direct active repository rulesets provide the required protections even when
# other inherited rules also apply. Do not read an omitted bypass list as empty.
def rules($target; $covered_patterns; $bypass):
  [.[] | select(
    .source == $repo and .source_type == "Repository"
    and .target == $target and .enforcement == "active"
    and .bypass_actors == $bypass
    and .conditions.ref_name.exclude == []
    and any(.conditions.ref_name.include[]; . as $pattern | $covered_patterns | index($pattern) != null)
  ) | .rules[]];

rules("branch"; ["refs/heads/main", "~DEFAULT_BRANCH", "~ALL"]; []) as $main
| rules("tag"; ["refs/tags/v*", "~ALL"]; []) as $immutable
| rules("tag"; ["refs/tags/v*", "~ALL"];
    [{"actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always"}]) as $creation
| any($main[]; .type == "deletion")
  and any($main[]; .type == "non_fast_forward")
  and any($main[];
    .type == "pull_request"
    and .parameters.allowed_merge_methods == ["squash"]
    and .parameters.required_review_thread_resolution == true)
  and any($main[];
    .type == "required_status_checks"
    and .parameters.strict_required_status_checks_policy == true
    and .parameters.do_not_enforce_on_create == false
    and any(.parameters.required_status_checks[]; .context == "check" and .integration_id == 15368))
  and any($immutable[]; .type == "update" and (.parameters.update_allows_fetch_and_merge // false) == false)
  and any($immutable[]; .type == "deletion")
  and any($creation[]; .type == "creation")
