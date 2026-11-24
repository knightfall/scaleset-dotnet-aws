"""Regression checks for local release bundle identity and byte verification."""

import hashlib
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
            digest = "sha256:" + "a" * 64
            payload = json.dumps({"manifests": [{"digest": digest}]}).encode()
            with tarfile.open(self.root / f"{name}.tar.gz", "w") as archive:
                member = tarfile.TarInfo("index.json")
                member.size = len(payload)
                archive.addfile(member, io.BytesIO(payload))
            (self.root / f"{name}.spdx.json").write_text(
                json.dumps({"spdxVersion": "SPDX-2.3", "packages": [{"name": "fixture"}]})
            )
            (self.root / f"{name}.grype.json").write_text(json.dumps({
                "source": {"type": "image", "target": {
                    "os": "linux", "architecture": "amd64" if rid == "linux-x64" else "arm64",
                    "manifestDigest": digest,
                }},
                "descriptor": {"name": "grype", "version": "fixture", "db": {"status": {
                    "schemaVersion": "v6", "built": "2026-11-24T00:00:00Z", "valid": True,
                }}},
                "matches": [],
            }))
        (self.root / "LICENSE").write_text("Fixture license")
        prepared = self.run_tool("prepare")
        self.assertEqual(0, prepared.returncode, prepared.stderr)

    def run_tool(self, operation, commit=None, attempt=2, digest=None):
        command = [sys.executable, str(self.tool), operation, str(self.root), "--commit", commit or self.commit]
        if attempt is not None:
            command.extend(["--run-id", "12345", "--attempt", str(attempt)])
        if digest is not None:
            command.extend(["--manifest-sha256", digest])
        return subprocess.run(command, capture_output=True, text=True, check=False)

    def test_round_trips_the_exact_candidate(self):
        digest = hashlib.sha256((self.root / "release.json").read_bytes()).hexdigest()
        for attempt in (2, None):
            verified = self.run_tool("verify", attempt=attempt, digest=digest)
            self.assertEqual(0, verified.returncode, verified.stderr)

    def test_rejects_another_commit_or_run_attempt(self):
        for result in (self.run_tool("verify", "2" * 40), self.run_tool("verify", attempt=1)):
            self.assertNotEqual(0, result.returncode)
            self.assertIn("identity or artifact checksums differ", result.stderr)

    def test_public_verifier_uses_the_attested_payload_digests(self):
        name = "scaleset-aws-controller-linux-x64.tar.gz"
        (self.root / name).write_bytes(b"replacement fixture")
        checksums = self.root / "SHA256SUMS"
        lines = []
        for line in checksums.read_text().splitlines():
            _, filename = line.split("  ", 1)
            lines.append(f"{hashlib.sha256((self.root / filename).read_bytes()).hexdigest()}  {filename}\n")
        checksums.write_text("".join(lines))
        result = self.run_tool("verify", attempt=None)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("differs from its verified manifest", result.stderr)

    def test_rejects_a_different_reviewed_manifest_digest(self):
        result = self.run_tool("verify", digest="0" * 64)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("differs from the reviewed SHA-256", result.stderr)

    def test_rejects_extra_release_assets(self):
        (self.root / "unexpected").touch()
        result = self.run_tool("verify")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("asset allowlist", result.stderr)

    def test_sealing_requires_a_valid_matching_scan_without_unreviewed_severity(self):
        (self.root / "release.json").unlink()
        (self.root / "SHA256SUMS").unlink()
        path = self.root / "scaleset-aws-controller-linux-arm64.grype.json"
        original = path.read_text()
        path.unlink()
        result = self.run_tool("prepare")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("asset allowlist", result.stderr)

        for reason, change in (
            ("does not identify the tested OCI manifest", lambda scan: scan["source"]["target"].update(architecture="amd64")),
            ("valid Grype database evidence", lambda scan: scan["descriptor"]["db"]["status"].update(valid=False)),
            ("blocking or unclassified vulnerability", lambda scan: scan["matches"].append({"vulnerability": {"severity": "High"}})),
        ):
            with self.subTest(reason=reason):
                scan = json.loads(original)
                change(scan)
                path.write_text(json.dumps(scan))
                result = self.run_tool("prepare")
                self.assertNotEqual(0, result.returncode)
                self.assertIn(reason, result.stderr)


if __name__ == "__main__":
    unittest.main()
