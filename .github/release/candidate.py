#!/usr/bin/env python3
"""Assemble and verify the exact controller release assets."""

import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import tarfile

PLATFORMS = {"linux-x64": "linux/amd64", "linux-arm64": "linux/arm64"}
MANIFEST = "release.json"
CHECKSUMS = "SHA256SUMS"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1_048_576), b""):
            digest.update(block)
    return digest.hexdigest()


def payload_names():
    return {"LICENSE"} | {f"scaleset-aws-controller-{rid}.{suffix}"
                          for rid in PLATFORMS for suffix in ("tar.gz", "spdx.json", "grype.json")}


def image_scan(directory, name, platform, digest):
    report = json.loads((directory / name).read_text(encoding="utf-8"))
    source = report["source"]
    target = source["target"]
    if (source["type"] != "image" or f"{target['os']}/{target['architecture']}" != platform
            or target["manifestDigest"] != digest):
        raise ValueError(f"Scan does not identify the tested OCI manifest: {name}")

    descriptor = report["descriptor"]
    database = descriptor["db"]["status"]
    if (descriptor["name"] != "grype" or not descriptor["version"]
            or database["valid"] is not True or not database["schemaVersion"]
            or datetime.fromisoformat(database["built"].replace("Z", "+00:00")).utcoffset() is None):
        raise ValueError(f"Scan lacks valid Grype database evidence: {name}")

    counts = {}
    matches = report["matches"]
    if not isinstance(matches, list):
        raise ValueError(f"Expected a vulnerability match list: {name}")
    for match in matches:
        severity = match["vulnerability"]["severity"].lower()
        if severity not in {"negligible", "low", "medium"}:
            raise ValueError(f"Scan has a blocking or unclassified vulnerability: {name}")
        counts[severity] = counts.get(severity, 0) + 1
    return {"report": name, "scannerVersion": descriptor["version"], "databaseSchema": database["schemaVersion"],
            "databaseBuiltAt": database["built"], "matchesBySeverity": counts}


def manifests(directory, commit, run_id, attempt, claimed_assets=None):
    if (not re.fullmatch(r"[0-9a-f]{40}", commit)
            or type(run_id) is not int or run_id <= 0 or type(attempt) is not int or attempt <= 0):
        raise ValueError("Expected a Git commit, positive run ID and positive attempt.")

    assets = {}
    images = {}
    for name in sorted(payload_names()):
        path = directory / name
        if not path.stat().st_size:
            raise ValueError(f"Empty release asset: {name}")
        assets[name] = {"sha256": sha256(path), "size": path.stat().st_size}
        if claimed_assets is not None and claimed_assets[name] != assets[name]:
            raise ValueError(f"Release asset differs from its verified manifest: {name}")

    for rid, platform in PLATFORMS.items():
        image = f"scaleset-aws-controller-{rid}.tar.gz"
        sbom = f"scaleset-aws-controller-{rid}.spdx.json"
        scan = f"scaleset-aws-controller-{rid}.grype.json"
        with tarfile.open(directory / image, "r:*") as archive:
            [descriptor] = json.load(archive.extractfile("index.json"))["manifests"]
        digest = descriptor["digest"]
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
            raise ValueError(f"Invalid OCI manifest digest for {rid}.")
        inventory = json.loads((directory / sbom).read_text(encoding="utf-8"))
        if not re.fullmatch(r"SPDX-2\.[0-9]+", inventory["spdxVersion"]) or not inventory.get("packages"):
            raise ValueError(f"Expected a populated SPDX image inventory for {rid}.")
        images[rid] = {"platform": platform, "archive": image, "manifestDigest": digest, "sbom": sbom,
                       "scan": image_scan(directory, scan, platform, digest)}

    data = {"schemaVersion": 2, "commit": commit, "runId": run_id, "runAttempt": attempt,
            "assets": assets, "images": images}
    return data


def checksum_text(directory, assets):
    digests = {name: entry["sha256"] for name, entry in assets.items()}
    digests[MANIFEST] = sha256(directory / MANIFEST)
    return "".join(f"{digest}  {name}\n" for name, digest in sorted(digests.items()))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("prepare", "verify"))
    parser.add_argument("directory", type=Path)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--run-id", type=int)
    parser.add_argument("--attempt", type=int)
    parser.add_argument("--manifest-sha256", help="Expected SHA-256 of the reviewed release.json")
    options = parser.parse_args()
    directory = options.directory
    if options.operation == "prepare" and (options.run_id is None or options.attempt is None):
        parser.error("prepare requires --run-id and --attempt")
    if options.manifest_sha256 is not None and (options.operation != "verify"
            or not re.fullmatch(r"[0-9a-f]{64}", options.manifest_sha256)):
        parser.error("--manifest-sha256 requires verify and a lowercase SHA-256")

    try:
        # Authenticate every payload before opening the large OCI and SPDX archives during promotion.
        entries = {entry.name for entry in directory.iterdir()}
        payloads = payload_names()
        expected = payloads if options.operation == "prepare" else payloads | {MANIFEST, CHECKSUMS}
        if entries != expected or any(entry.is_symlink() or not entry.is_file() for entry in directory.iterdir()):
            raise ValueError("Candidate directory does not match the release asset allowlist.")

        manifest = directory / MANIFEST
        checksums = directory / CHECKSUMS
        if options.manifest_sha256 is not None and sha256(manifest) != options.manifest_sha256:
            raise ValueError("Release manifest differs from the reviewed SHA-256.")
        claimed = json.loads(manifest.read_text(encoding="utf-8")) if options.operation == "verify" else None
        run_id = options.run_id if options.run_id is not None else claimed["runId"]
        attempt = options.attempt if options.attempt is not None else claimed["runAttempt"]
        data = manifests(directory, options.commit, run_id, attempt,
                         claimed["assets"] if claimed is not None else None)
        if options.operation == "prepare":
            manifest.write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")
            checksums.write_text(checksum_text(directory, data["assets"]), encoding="utf-8")
        elif (claimed != data or
              checksums.read_text(encoding="utf-8") != checksum_text(directory, data["assets"])):
            raise ValueError("Candidate identity or artifact checksums differ.")
    except (OSError, ValueError, KeyError, TypeError, AttributeError, tarfile.TarError) as error:
        parser.exit(1, f"Release candidate rejected: {error}\n")


if __name__ == "__main__":
    main()
