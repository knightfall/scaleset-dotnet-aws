"""Regression checks for local release bundle identity and byte verification."""

from datetime import datetime, timedelta, timezone
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest

from candidate import verify_promotion_freshness


class CandidateTests(unittest.TestCase):
    commit = "1" * 40
    tool = Path(__file__).with_name("candidate.py")

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        for rid in ("linux-x64", "linux-arm64"):
            name = f"scaleset-aws-controller-{rid}"
            architecture = "amd64" if rid == "linux-x64" else "arm64"
            config = json.dumps({"os": "linux", "architecture": architecture}).encode()
            config_descriptor = self.descriptor(config, "application/vnd.oci.image.config.v1+json")
            manifest = json.dumps({"schemaVersion": 2, "config": config_descriptor, "layers": []}).encode()
            image_descriptor = self.descriptor(manifest, "application/vnd.oci.image.manifest.v1+json")
            digest = image_descriptor["digest"]
            archive_name = f"{name}.tar.gz"
            self.write_archive(self.root / archive_name, {
                "oci-layout": b'{"imageLayoutVersion":"1.0.0"}',
                "index.json": json.dumps({"manifests": [image_descriptor]}).encode(),
                f"blobs/sha256/{digest[7:]}": manifest,
                f"blobs/sha256/{config_descriptor['digest'][7:]}": config,
            })
            source = f"SPDXRef-DocumentRoot-Image-{archive_name}"
            (self.root / f"{name}.spdx.json").write_text(json.dumps({
                "spdxVersion": "SPDX-2.3", "name": archive_name, "SPDXID": "SPDXRef-DOCUMENT",
                "packages": [{"SPDXID": "SPDXRef-Package-fixture", "name": "fixture"}, {
                    "SPDXID": source, "name": archive_name, "primaryPackagePurpose": "CONTAINER",
                    "versionInfo": digest, "checksums": [{"algorithm": "SHA256", "checksumValue": digest[7:]}],
                    "externalRefs": [{"referenceCategory": "PACKAGE-MANAGER", "referenceType": "purl",
                                      "referenceLocator": f"pkg:oci/{archive_name}@sha256%3A{digest[7:]}?arch={architecture}"}],
                }],
                "relationships": [{"spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES",
                                   "relatedSpdxElement": source}],
            }))
            (self.root / f"{name}.grype.json").write_text(json.dumps({
                "source": {"type": "image", "target": {
                    "os": "linux", "architecture": architecture, "manifestDigest": digest,
                    "imageID": config_descriptor["digest"],
                }},
                "descriptor": {"name": "grype", "version": "fixture", "db": {"status": {
                    "schemaVersion": "v6", "built": "2026-11-24T00:00:00Z", "valid": True,
                }}},
                "matches": [],
            }))
        (self.root / "LICENSE").write_text("Fixture license")
        prepared = self.run_tool("prepare")
        self.assertEqual(0, prepared.returncode, prepared.stderr)

    @staticmethod
    def descriptor(content, media_type):
        return {"mediaType": media_type, "size": len(content),
                "digest": "sha256:" + hashlib.sha256(content).hexdigest()}

    @staticmethod
    def write_archive(path, files):
        with tarfile.open(path, "w:gz") as archive:
            for name, content in files.items():
                member = tarfile.TarInfo(name)
                member.size = len(content)
                archive.addfile(member, io.BytesIO(content))

    def run_tool(self, operation, commit=None, attempt=2, digest=None, promotion=False):
        command = [sys.executable, str(self.tool), operation, str(self.root), "--commit", commit or self.commit]
        if attempt is not None:
            command.extend(["--run-id", "12345", "--attempt", str(attempt)])
        if digest is not None:
            command.extend(["--manifest-sha256", digest])
        if promotion:
            command.append("--promotion")
        return subprocess.run(command, capture_output=True, text=True, check=False)

    def check_image(self, rid="linux-arm64"):
        return subprocess.run([sys.executable, str(self.tool), "check-image", str(self.root), "--rid", rid],
                              capture_output=True, text=True, check=False)

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
            ("does not identify the tested OCI manifest", lambda scan: scan["source"]["target"].update(imageID="sha256:" + "a" * 64)),
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

    def test_native_check_links_archive_and_inventory_to_the_same_oci_manifest(self):
        data = json.loads((self.root / "release.json").read_text())
        self.assertEqual(data["schemaVersion"], 3)
        for rid, image in data["images"].items():
            result = self.check_image(rid)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual(image, json.loads(result.stdout))
            self.assertEqual(image["sbomSubject"], f"SPDXRef-DocumentRoot-Image-{image['archive']}")

    def test_rejects_swapped_or_unrelated_well_formed_inventory(self):
        path = self.root / "scaleset-aws-controller-linux-arm64.spdx.json"
        original = path.read_text()
        path.write_bytes((self.root / "scaleset-aws-controller-linux-x64.spdx.json").read_bytes())
        result = self.check_image()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("SPDX subject does not identify", result.stderr)
        for change, reason in (
            (lambda doc: doc["packages"][1].update(versionInfo="sha256:" + "f" * 64), "SPDX subject does not identify"),
            (lambda doc: doc["packages"][1]["checksums"][0].update(checksumValue="f" * 64), "SPDX subject does not identify"),
            (lambda doc: doc["packages"][1]["externalRefs"][0].update(referenceLocator=doc["packages"][1]
                ["externalRefs"][0]["referenceLocator"].replace("arch=arm64", "arch=amd64")), "SPDX package URL"),
            (lambda doc: doc["relationships"][0].update(relatedSpdxElement="SPDXRef-Package-fixture"), "SPDX subject does not identify"),
        ):
            with self.subTest(reason=reason):
                inventory = json.loads(original)
                change(inventory)
                path.write_text(json.dumps(inventory))
                result = self.check_image()
                self.assertNotEqual(0, result.returncode)
                self.assertIn(reason, result.stderr)

    def test_image_config_and_manifest_are_read_from_verified_archive_blobs(self):
        arm64 = self.root / "scaleset-aws-controller-linux-arm64.tar.gz"
        with tarfile.open(arm64, "r:gz") as archive:
            files = {member.name: archive.extractfile(member).read() for member in archive}
        [image] = json.loads(files["index.json"])["manifests"]
        manifest_path = f"blobs/sha256/{image['digest'][7:]}"
        files[manifest_path] = files[manifest_path].replace(b"layers", b"layerz")
        self.write_archive(arm64, files)
        result = self.check_image()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("OCI blob digest differs", result.stderr)

        arm64.write_bytes((self.root / "scaleset-aws-controller-linux-x64.tar.gz").read_bytes())
        result = self.check_image()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("OCI config does not identify the selected platform", result.stderr)

    def test_promotion_age_checks_both_scans_with_an_inclusive_limit(self):
        data = json.loads((self.root / "release.json").read_text())
        built = datetime(2026, 11, 24, tzinfo=timezone.utc)
        verify_promotion_freshness(data, built + timedelta(hours=72))
        for now in (built + timedelta(hours=72, microseconds=1), built - timedelta(microseconds=1)):
            with self.subTest(now=now):
                with self.assertRaisesRegex(ValueError, "72-hour promotion window"):
                    verify_promotion_freshness(data, now)

    def test_expired_scan_is_rejected_for_promotion_but_stays_verifiable_as_history(self):
        (self.root / "release.json").unlink()
        (self.root / "SHA256SUMS").unlink()
        path = self.root / "scaleset-aws-controller-linux-x64.grype.json"
        scan = json.loads(path.read_text())
        scan["descriptor"]["db"]["status"]["built"] = "2000-01-01T00:00:00Z"
        path.write_text(json.dumps(scan))
        prepared = self.run_tool("prepare")
        self.assertEqual(0, prepared.returncode, prepared.stderr)
        verified = self.run_tool("verify")
        self.assertEqual(0, verified.returncode, verified.stderr)
        promoted = self.run_tool("verify", promotion=True)
        self.assertNotEqual(0, promoted.returncode)
        self.assertIn("linux-x64 Grype database evidence is outside the 72-hour promotion window", promoted.stderr)


if __name__ == "__main__":
    unittest.main()
