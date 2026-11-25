"""Exercise the release gate with GitHub metadata fixtures and a local tag stub."""

import copy
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class PreflightTests(unittest.TestCase):
    repo = "knightfall/scaleset-dotnet-aws"
    commit = "1" * 40
    root = Path(__file__).resolve().parents[2]
    artifact = f"controller-candidate-{commit}-2"
    rulesets = "rulesets?includes_parents=true&targets=branch,tag&per_page=100"

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.scratch = Path(temporary.name)
        self.output = self.scratch / "output"
        self.env = dict(os.environ, PATH=f"{self.scratch}{os.pathsep}{os.environ['PATH']}",
                        GITHUB_REPOSITORY=self.repo, GITHUB_REF="refs/heads/main", GITHUB_SHA=self.commit,
                        GITHUB_OUTPUT=str(self.output), RELEASE_TAG="v0.1.0-alpha.1",
                        CANDIDATE_RUN_ID="12345", CANDIDATE_ATTEMPT="2", CANDIDATE_MANIFEST_SHA256="a" * 64,
                        GH_TOKEN="workflow-token", TEST_TAG_COMMIT=self.commit)
        self.write_program("gh", """
import json
import os
import sys

args = sys.argv[2:]
if args[:2] in [["--method", "GET"], ["--paginate", "--slurp"]]:
    args = args[2:]
segments = args[0].split("/", 3)
key = segments[3] if len(segments) == 4 else "repository"
assert "RELEASE_SETTINGS_READ_TOKEN" not in os.environ
if key in ("repository", "immutable-releases") or key.startswith("rulesets"):
    assert os.environ["GH_TOKEN"] == "settings-token"
else:
    assert os.environ["GH_TOKEN"] == "workflow-token"
data = json.loads(os.environ["TEST_GITHUB_API"])[key]
if "--jq" in args:
    data = data[args[args.index("--jq") + 1].removeprefix(".")]
print(json.dumps(data))
""")
        self.write_program("git", """
import os

ref = "refs/tags/" + os.environ["RELEASE_TAG"]
print("2" * 40, ref)
print(os.environ["TEST_TAG_COMMIT"], ref + "^{}")
""")
        current = {"head_branch": "main", "head_sha": self.commit, "event": "push", "status": "completed",
                   "conclusion": "success", "head_repository": {"full_name": self.repo}}
        created = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        branch = self.rules(11, "branch", "~DEFAULT_BRANCH", [], [
            {"type": "deletion"}, {"type": "non_fast_forward"},
            {"type": "pull_request", "parameters": {
                "allowed_merge_methods": ["squash"], "required_review_thread_resolution": True}},
            {"type": "required_status_checks", "parameters": {
                "do_not_enforce_on_create": False, "strict_required_status_checks_policy": True,
                "required_status_checks": [{"context": "check", "integration_id": 15368}]}},
        ])
        immutable = self.rules(12, "tag", "refs/tags/v*", [], [{"type": "update"}, {"type": "deletion"}])
        creation = self.rules(13, "tag", "refs/tags/v*", [
            {"actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always"},
        ], [{"type": "creation"}])
        self.api = {
            "immutable-releases": {"enabled": True},
            "repository": {"default_branch": "main"},
            self.rulesets: [[{key: ruleset[key] for key in ("id", "target", "enforcement", "source", "source_type")}
                             for ruleset in (branch, immutable, creation)]],
            "rulesets/11": branch, "rulesets/12": immutable, "rulesets/13": creation,
            "environments/production": {"can_admins_bypass": False,
                                        "deployment_branch_policy": {"custom_branch_policies": True},
                                        "protection_rules": [{"type": "required_reviewers", "reviewers": [{"id": 1}]}]},
            "environments/production/deployment-branch-policies": {
                "total_count": 1, "branch_policies": [{"name": "main", "type": "branch"}]},
            "actions/workflows/release-candidate.yml": {"id": 15},
            "actions/runs/12345/attempts/2": dict(current, id=12345, workflow_id=15, run_attempt=2),
            "actions/workflows/ci.yml/runs": {"workflow_runs": [current]},
            "actions/runs/12345/artifacts": {"artifacts": [
                {"id": 42, "name": self.artifact, "expired": False, "created_at": created}]},
        }

    def rules(self, identifier, target, pattern, bypass, rules):
        return {"id": identifier, "source": self.repo, "source_type": "Repository", "target": target, "enforcement": "active",
                "conditions": {"ref_name": {"include": [pattern], "exclude": []}}, "bypass_actors": bypass, "rules": rules}

    def write_program(self, name, source):
        path = self.scratch / name
        path.write_text("#!/usr/bin/env python3\n" + source)
        path.chmod(0o700)

    def run_gate(self, api=None, script="preflight", args=(), **env):
        self.output.unlink(missing_ok=True)
        return subprocess.run(
            ["bash", f".github/release/{script}.sh", *args], cwd=self.root, capture_output=True, text=True,
            env=dict(self.env, **env, TEST_GITHUB_API=json.dumps(api or self.api)), check=False,
        )

    def test_selects_exact_artifact_even_after_a_later_run_attempt(self):
        api = copy.deepcopy(self.api)
        api["actions/runs/12345"] = dict(api["actions/runs/12345/attempts/2"], run_attempt=3)
        api["actions/runs/12345/artifacts"]["artifacts"].append({
            "id": 43, "name": f"controller-candidate-{self.commit}-3", "expired": False,
        })
        result = self.run_gate(api)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("artifact_id=42\n", self.output.read_text())

    def test_rejects_wrong_tag_or_missing_review_identity(self):
        for env, error in (
            ({"TEST_TAG_COMMIT": "8" * 40}, "tag does not resolve"),
            ({"CANDIDATE_ATTEMPT": ""}, "Candidate attempt"),
            ({"CANDIDATE_MANIFEST_SHA256": ""}, "reviewed manifest SHA-256"),
        ):
            with self.subTest(env=env):
                result = self.run_gate(**env)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(error, result.stderr)
                self.assertFalse(self.output.exists())

    def test_rejects_replaced_attempt_or_stale_release_evidence(self):
        for endpoint, key, value, error in (
            ("actions/runs/12345/attempts/2", "run_attempt", 3, "exact commit"),
            ("actions/runs/12345/attempts/2", "head_sha", "8" * 40, "exact commit"),
            ("actions/runs/12345/attempts/2", "event", "pull_request", "exact commit"),
            ("actions/workflows/ci.yml/runs", "workflow_runs", [], "main CI push run"),
            ("actions/runs/12345/artifacts", "artifacts", [{"id": 42, "name": self.artifact, "expired": True}],
             "unexpired sealed candidate"),
        ):
            with self.subTest(endpoint=endpoint, key=key):
                api = copy.deepcopy(self.api)
                api[endpoint][key] = value
                result = self.run_gate(api)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(error, result.stderr)
                self.assertFalse(self.output.exists())

    def test_rechecks_exact_artifact_after_approval(self):
        result = self.run_gate(REVIEWED_ARTIFACT_ID="42")
        self.assertEqual(0, result.returncode, result.stderr)
        for reviewed in ("", "43"):
            with self.subTest(reviewed=reviewed):
                result = self.run_gate(REVIEWED_ARTIFACT_ID=reviewed)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("differs from the reviewed artifact", result.stderr)
                self.assertFalse(self.output.exists())

    def test_rejects_stale_future_and_missing_artifact_timestamps(self):
        now = datetime.now(timezone.utc)
        for value in (now - timedelta(days=4), now + timedelta(days=1), None):
            with self.subTest(value=value):
                api = copy.deepcopy(self.api)
                api["actions/runs/12345/artifacts"]["artifacts"][0]["created_at"] = (
                    value.strftime("%Y-%m-%dT%H:%M:%SZ") if value else None)
                result = self.run_gate(api)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("72-hour promotion window", result.stderr)

    def test_production_checks_protections_with_scoped_settings_token(self):
        result = self.run_gate(script="production", RELEASE_SETTINGS_READ_TOKEN="settings-token")
        self.assertEqual(0, result.returncode, result.stderr)
        result = self.run_gate(script="production", RELEASE_SETTINGS_READ_TOKEN="")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Configure RELEASE_SETTINGS_READ_TOKEN", result.stderr)

        for endpoint, key, value, error in (
            ("immutable-releases", "enabled", False, "immutable releases"),
            ("repository", "default_branch", "develop", "repository default branch"),
            ("environments/production", "can_admins_bypass", True, "administrator bypass"),
            ("environments/production/deployment-branch-policies", "total_count", 2, "only the main branch"),
        ):
            with self.subTest(endpoint=endpoint, key=key):
                api = copy.deepcopy(self.api)
                api[endpoint][key] = value
                result = self.run_gate(api, script="production", RELEASE_SETTINGS_READ_TOKEN="settings-token")
                self.assertNotEqual(0, result.returncode)
                self.assertIn(error, result.stderr)

    def test_production_rejects_effective_rules_and_bypass_drift(self):
        for path, mutate in (
            ("rulesets/11", lambda rule: rule.update(enforcement="evaluate")),
            ("rulesets/11", lambda rule: rule.pop("bypass_actors")),
            ("rulesets/11", lambda rule: rule["rules"][2]["parameters"].update(allowed_merge_methods=["squash", "merge"])),
            ("rulesets/11", lambda rule: rule["rules"][2]["parameters"].update(required_review_thread_resolution=False)),
            ("rulesets/11", lambda rule: rule["rules"][3]["parameters"].update(strict_required_status_checks_policy=False)),
            ("rulesets/11", lambda rule: rule["rules"][3]["parameters"]["required_status_checks"][0].update(integration_id=None)),
            ("rulesets/12", lambda rule: rule["conditions"]["ref_name"].update(include=["refs/tags/v0.1.0-alpha.1"])),
            ("rulesets/12", lambda rule: rule["conditions"]["ref_name"].update(exclude=["refs/tags/v0.1.0-alpha.1"])),
            ("rulesets/12", lambda rule: rule.update(rules=[{"type": "update"}])),
            ("rulesets/12", lambda rule: rule.update(bypass_actors=self.api["rulesets/13"]["bypass_actors"])),
            ("rulesets/13", lambda rule: rule["bypass_actors"][0].update(actor_id=4)),
            ("rulesets/13", lambda rule: rule.pop("bypass_actors")),
        ):
            with self.subTest(endpoint=path, mutate=mutate):
                api = copy.deepcopy(self.api)
                mutate(api[path])
                result = self.run_gate(api, script="production", RELEASE_SETTINGS_READ_TOKEN="settings-token")
                self.assertNotEqual(0, result.returncode)
                self.assertIn("explicit bypass evidence", result.stderr)
                self.assertNotIn("settings-token", result.stderr)

    def test_pre_tag_settings_only_uses_the_same_bypass_predicate(self):
        api = copy.deepcopy(self.api)
        del api["environments/production"]
        result = self.run_gate(api, script="production", args=("--settings-only",),
                               RELEASE_SETTINGS_READ_TOKEN="settings-token")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("Read-only repository release settings verified.\n", result.stdout)
        self.assertEqual("", result.stderr)

        for ruleset in ("rulesets/11", "rulesets/12", "rulesets/13"):
            with self.subTest(ruleset=ruleset):
                incomplete = copy.deepcopy(api)
                del incomplete[ruleset]["bypass_actors"]
                result = self.run_gate(incomplete, script="production", args=("--settings-only",),
                                       RELEASE_SETTINGS_READ_TOKEN="settings-token")
                self.assertNotEqual(0, result.returncode)
                self.assertIn("explicit bypass evidence", result.stderr)
                self.assertEqual("", result.stdout)
                self.assertNotIn("settings-token", result.stderr)

    def test_production_fails_closed_when_settings_queries_fail(self):
        for endpoint, reason in (
            ("immutable-releases", "Enable immutable releases"),
            (self.rulesets, "Could not discover active repository"),
            ("rulesets/12", "Could not read an active repository ruleset"),
            ("environments/production", "Production must have required reviewers"),
        ):
            with self.subTest(endpoint=endpoint):
                api = copy.deepcopy(self.api)
                del api[endpoint]
                result = self.run_gate(api, script="production", RELEASE_SETTINGS_READ_TOKEN="settings-token")
                self.assertNotEqual(0, result.returncode)
                self.assertIn(reason, result.stderr)
                self.assertNotIn("settings-token", result.stderr)


if __name__ == "__main__":
    unittest.main()
