"""Exercise the release gate with GitHub metadata fixtures and a local tag stub."""

import copy
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
if args[:2] == ["--method", "GET"]:
    args = args[2:]
key = args[0].split("/", 3)[3]
if key == "immutable-releases":
    assert os.environ["GH_TOKEN"] == "settings-token"
else:
    assert os.environ["GH_TOKEN"] == "workflow-token"
    assert "RELEASE_SETTINGS_READ_TOKEN" not in os.environ
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
        self.api = {
            "immutable-releases": {"enabled": True},
            "environments/production": {"can_admins_bypass": False,
                                        "deployment_branch_policy": {"custom_branch_policies": True},
                                        "protection_rules": [{"type": "required_reviewers", "reviewers": [{"id": 1}]}]},
            "environments/production/deployment-branch-policies": {
                "total_count": 1, "branch_policies": [{"name": "main", "type": "branch"}]},
            "actions/workflows/release-candidate.yml": {"id": 15},
            "actions/runs/12345/attempts/2": dict(current, id=12345, workflow_id=15, run_attempt=2),
            "actions/workflows/ci.yml/runs": {"workflow_runs": [current]},
            "actions/runs/12345/artifacts": {"artifacts": [{"id": 42, "name": self.artifact, "expired": False}]},
        }

    def write_program(self, name, source):
        path = self.scratch / name
        path.write_text("#!/usr/bin/env python3\n" + source)
        path.chmod(0o700)

    def run_gate(self, api=None, script="preflight", **env):
        self.output.unlink(missing_ok=True)
        return subprocess.run(
            ["bash", f".github/release/{script}.sh"], cwd=self.root, capture_output=True, text=True,
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

    def test_production_checks_protections_with_scoped_settings_token(self):
        result = self.run_gate(script="production", RELEASE_SETTINGS_READ_TOKEN="settings-token")
        self.assertEqual(0, result.returncode, result.stderr)
        result = self.run_gate(script="production", RELEASE_SETTINGS_READ_TOKEN="")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Configure RELEASE_SETTINGS_READ_TOKEN", result.stderr)

        for endpoint, key, value, error in (
            ("immutable-releases", "enabled", False, "immutable releases"),
            ("environments/production", "can_admins_bypass", True, "administrator bypass"),
            ("environments/production/deployment-branch-policies", "total_count", 2, "only the main branch"),
        ):
            with self.subTest(endpoint=endpoint, key=key):
                api = copy.deepcopy(self.api)
                api[endpoint][key] = value
                result = self.run_gate(api, script="production", RELEASE_SETTINGS_READ_TOKEN="settings-token")
                self.assertNotEqual(0, result.returncode)
                self.assertIn(error, result.stderr)


if __name__ == "__main__":
    unittest.main()
