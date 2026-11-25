#!/usr/bin/env python3
"""Assemble and verify the exact controller release assets."""

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import re
import tarfile
from urllib.parse import parse_qs, unquote

PLATFORMS = {"linux-x64": "linux/amd64", "linux-arm64": "linux/arm64"}
MANIFEST = "release.json"
CHECKSUMS = "SHA256SUMS"
MAX_PROMOTION_AGE = timedelta(hours=72)
OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json"
OCI_CONFIG = "application/vnd.oci.image.config.v1+json"
SHA256_DIGEST = re.compile(r"sha256:[0-9a-f]{64}")


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1_048_576), b""):
            digest.update(block)
    return digest.hexdigest()


def payload_names():
    return {"LICENSE"} | {f"scaleset-aws-controller-{rid}.{suffix}"
                          for rid in PLATFORMS for suffix in ("tar.gz", "spdx.json", "grype.json")}


def oci_blob(archive, descriptor, media_type):
    digest = descriptor["digest"]
    if descriptor["mediaType"] != media_type or not SHA256_DIGEST.fullmatch(digest):
        raise ValueError("Invalid OCI media type or SHA-256 descriptor.")
    member = archive.getmember(f"blobs/sha256/{digest[7:]}")
    if not member.isfile() or member.size != descriptor["size"]:
        raise ValueError("OCI blob size differs from its descriptor.")
    with archive.extractfile(member) as stream:
        contents = stream.read()
    if hashlib.sha256(contents).hexdigest() != digest[7:]:
        raise ValueError("OCI blob digest differs from its descriptor.")
    return json.loads(contents)


def image_identity(path, platform):
    with tarfile.open(path, "r:*") as archive:
        with archive.extractfile("oci-layout") as layout:
            if json.load(layout)["imageLayoutVersion"] != "1.0.0":
                raise ValueError(f"Expected an OCI image layout: {path.name}")
        with archive.extractfile("index.json") as index:
            [descriptor] = json.load(index)["manifests"]
        manifest = oci_blob(archive, descriptor, OCI_MANIFEST)
        config = oci_blob(archive, manifest["config"], OCI_CONFIG)
    if f"{config['os']}/{config['architecture']}" != platform:
        raise ValueError(f"OCI config does not identify the selected platform: {path.name}")
    return descriptor["digest"], manifest["config"]["digest"]


def image_sbom(directory, name, archive, architecture, digest):
    inventory = json.loads((directory / name).read_text(encoding="utf-8"))
    packages = inventory["packages"]
    if not re.fullmatch(r"SPDX-2\.[0-9]+", inventory["spdxVersion"]) or not isinstance(packages, list) or len(packages) < 2:
        raise ValueError(f"Expected a populated SPDX image inventory: {name}")
    roots = [package for package in packages if package.get("primaryPackagePurpose") == "CONTAINER"]
    if len(roots) != 1:
        raise ValueError(f"Expected one SPDX image subject: {name}")
    root = roots[0]
    subject = root["SPDXID"]
    relationships = [item["relatedSpdxElement"] for item in inventory["relationships"]
                     if item["spdxElementId"] == inventory["SPDXID"] and item["relationshipType"] == "DESCRIBES"]
    if (inventory["SPDXID"] != "SPDXRef-DOCUMENT" or subject != f"SPDXRef-DocumentRoot-Image-{archive}"
            or relationships != [subject] or sum(package["SPDXID"] == subject for package in packages) != 1
            or inventory["name"] != archive or root["name"] != archive
            or root.get("versionInfo") != digest
            or root.get("checksums") != [{"algorithm": "SHA256", "checksumValue": digest[7:]}]):
        raise ValueError(f"SPDX subject does not identify the tested OCI manifest: {name}")
    [reference] = [item["referenceLocator"] for item in root["externalRefs"]
                   if item["referenceCategory"] == "PACKAGE-MANAGER" and item["referenceType"] == "purl"]
    coordinates, _, qualifiers = reference.partition("?")
    if (not coordinates.startswith("pkg:oci/") or
            unquote(coordinates.removeprefix("pkg:oci/")) != f"{root['name']}@{digest}" or
            parse_qs(qualifiers, keep_blank_values=True, strict_parsing=True) != {"arch": [architecture]}):
        raise ValueError(f"SPDX package URL does not identify the tested OCI platform and digest: {name}")
    return subject


def timestamp(value):
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.utcoffset() is None:
        raise ValueError("Evidence timestamp must specify its UTC offset.")
    return result


def image_scan(directory, name, platform, digest, config_digest):
    report = json.loads((directory / name).read_text(encoding="utf-8"))
    source = report["source"]
    target = source["target"]
    if (source["type"] != "image" or f"{target['os']}/{target['architecture']}" != platform
            or target["manifestDigest"] != digest or target["imageID"] != config_digest):
        raise ValueError(f"Scan does not identify the tested OCI manifest: {name}")

    descriptor = report["descriptor"]
    database = descriptor["db"]["status"]
    if (descriptor["name"] != "grype" or not descriptor["version"]
            or database["valid"] is not True or not database["schemaVersion"]):
        raise ValueError(f"Scan lacks valid Grype database evidence: {name}")
    timestamp(database["built"])

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


def image_evidence(directory, rid):
    platform = PLATFORMS[rid]
    image = f"scaleset-aws-controller-{rid}.tar.gz"
    sbom = f"scaleset-aws-controller-{rid}.spdx.json"
    scan = f"scaleset-aws-controller-{rid}.grype.json"
    digest, config_digest = image_identity(directory / image, platform)
    subject = image_sbom(directory, sbom, image, platform.split("/")[1], digest)
    return {"platform": platform, "archive": image, "manifestDigest": digest, "configDigest": config_digest,
            "sbom": sbom, "sbomSubject": subject, "scan": image_scan(directory, scan, platform, digest, config_digest)}


def manifests(directory, commit, run_id, attempt, claimed_assets=None):
    if (not re.fullmatch(r"[0-9a-f]{40}", commit)
            or type(run_id) is not int or run_id <= 0 or type(attempt) is not int or attempt <= 0):
        raise ValueError("Expected a Git commit, positive run ID and positive attempt.")

    assets = {}
    for name in sorted(payload_names()):
        path = directory / name
        if not path.stat().st_size:
            raise ValueError(f"Empty release asset: {name}")
        assets[name] = {"sha256": sha256(path), "size": path.stat().st_size}
        if claimed_assets is not None and claimed_assets[name] != assets[name]:
            raise ValueError(f"Release asset differs from its verified manifest: {name}")

    return {"schemaVersion": 3, "commit": commit, "runId": run_id, "runAttempt": attempt,
            "assets": assets, "images": {rid: image_evidence(directory, rid) for rid in PLATFORMS}}


def checksum_text(directory, assets):
    digests = {name: entry["sha256"] for name, entry in assets.items()}
    digests[MANIFEST] = sha256(directory / MANIFEST)
    return "".join(f"{digest}  {name}\n" for name, digest in sorted(digests.items()))


def verify_promotion_freshness(data, now):
    for rid, image in data["images"].items():
        age = now - timestamp(image["scan"]["databaseBuiltAt"])
        if not timedelta(0) <= age <= MAX_PROMOTION_AGE:
            raise ValueError(f"{rid} Grype database evidence is outside the 72-hour promotion window.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("prepare", "verify", "check-image"))
    parser.add_argument("directory", type=Path)
    parser.add_argument("--commit")
    parser.add_argument("--rid", choices=PLATFORMS)
    parser.add_argument("--run-id", type=int)
    parser.add_argument("--attempt", type=int)
    parser.add_argument("--manifest-sha256", help="Expected SHA-256 of the reviewed release.json")
    parser.add_argument("--promotion", action="store_true", help="Require current Grype database evidence")
    options = parser.parse_args()
    directory = options.directory
    if options.operation == "check-image":
        if not options.rid:
            parser.error("check-image requires --rid")
    elif not options.commit or options.rid is not None:
        parser.error("prepare and verify require --commit and do not take --rid")
    if options.operation == "prepare" and (options.run_id is None or options.attempt is None):
        parser.error("prepare requires --run-id and --attempt")
    if options.manifest_sha256 is not None and (options.operation != "verify"
            or not re.fullmatch(r"[0-9a-f]{64}", options.manifest_sha256)):
        parser.error("--manifest-sha256 requires verify and a lowercase SHA-256")
    if options.promotion and options.operation != "verify":
        parser.error("--promotion requires verify")

    try:
        if options.operation == "check-image":
            print(json.dumps(image_evidence(directory, options.rid), sort_keys=True))
            return

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
        if options.promotion:
            verify_promotion_freshness(data, datetime.now(timezone.utc))
    except (OSError, ValueError, KeyError, TypeError, AttributeError, tarfile.TarError) as error:
        parser.exit(1, f"Release candidate rejected: {error}\n")


if __name__ == "__main__":
    main()
