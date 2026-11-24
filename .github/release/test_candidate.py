"""Regression checks for local release bundle identity and byte verification."""

import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest


class CandidateTests(unittest.TestCase):
    commit = "1" * 40
    tool = Path(__file__).with_name("candidate.py")

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        for rid in ("linux-x64", "linux-arm64"):
            name = f"scaleset-aws-controller-{rid}"
            payload = json.dumps({"manifests": [{"digest": "sha256:" + "a" * 64}]}).encode()
            with tarfile.open(self.root / f"{name}.tar.gz", "w") as archive:
                member = tarfile.TarInfo("index.json")
                member.size = len(payload)
                archive.addfile(member, io.BytesIO(payload))
            (self.root / f"{name}.spdx.json").write_text(
                json.dumps({"spdxVersion": "SPDX-2.3", "packages": [{"name": "fixture"}]})
            )
        (self.root / "LICENSE").write_text("Fixture license")
        prepared = self.run_tool("prepare")
        self.assertEqual(0, prepared.returncode, prepared.stderr)

    def run_tool(self, operation, commit=None, attempt=2):
        return subprocess.run(
            [sys.executable, str(self.tool), operation, str(self.root), "--commit", commit or self.commit,
             "--run-id", "12345", "--attempt", str(attempt)],
            capture_output=True, text=True, check=False,
        )

    def test_round_trips_the_exact_candidate(self):
        verified = self.run_tool("verify")
        self.assertEqual(0, verified.returncode, verified.stderr)

    def test_rejects_another_commit_or_run_attempt(self):
        for result in (self.run_tool("verify", "2" * 40), self.run_tool("verify", attempt=1)):
            self.assertNotEqual(0, result.returncode)
            self.assertIn("identity or artifact checksums differ", result.stderr)

    def test_rejects_changed_payload_before_parsing_it(self):
        (self.root / "scaleset-aws-controller-linux-x64.tar.gz").write_bytes(b"changed")
        result = self.run_tool("verify")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("differs from its verified manifest", result.stderr)

    def test_rejects_extra_release_assets(self):
        (self.root / "unexpected").touch()
        result = self.run_tool("verify")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("asset allowlist", result.stderr)


if __name__ == "__main__":
    unittest.main()
