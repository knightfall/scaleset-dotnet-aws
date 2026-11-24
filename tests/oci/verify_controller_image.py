#!/usr/bin/env python3
"""Validate an offline SDK PublishContainer archive against the tested controller assembly."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import tarfile


CONTROLLER_DLL = "Knightfall.ScaleSet.Aws.Controller.dll"
OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json"
BASE_DIGESTS = {
    "amd64": "sha256:8489a9d9439265a2d8f794e71050389974d0b4652003d18a4690825c4d10f186",
    "arm64": "sha256:21d1dfbcec52a501cc2c1e3cb6b2386d7be827a1a3aa762482a942782618440d",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def blob_path(descriptor):
    algorithm, digest = descriptor["digest"].split(":", 1)
    require(algorithm == "sha256" and len(digest) == 64, "Expected an OCI SHA-256 descriptor.")
    return f"blobs/{algorithm}/{digest}"


def digest_stream(contents):
    digest = hashlib.sha256()
    for block in iter(lambda: contents.read(1_048_576), b""):
        digest.update(block)
    return digest.hexdigest()


def verify_blob(archive, descriptor):
    member = archive.getmember(blob_path(descriptor))
    require(member.isfile() and member.size == descriptor["size"], "OCI blob size differs from its descriptor.")
    with archive.extractfile(member) as contents:
        actual = digest_stream(contents)
    require("sha256:" + actual == descriptor["digest"], "OCI blob digest differs from its descriptor.")


def read_json(archive, name):
    with archive.extractfile(name) as contents:
        return json.load(contents)


def verify_application(archive, descriptor, assembly):
    managed_assembly = PurePosixPath("app", CONTROLLER_DLL)
    with archive.extractfile(blob_path(descriptor)) as contents:
        with tarfile.open(fileobj=contents, mode="r|*") as layer:
            found = False
            for member in layer:
                path = PurePosixPath(member.name.lstrip("/"))
                require(path.parts and path.parts[0] == "app"
                        and ".." not in path.parts and not any(part.startswith(".") for part in path.parts),
                        f"Unexpected application layer path {member.name}.")
                require(member.isdir() or member.isfile(), "The application layer must not use links or device nodes.")
                if member.isfile():
                    require(path.suffix in {".dll", ".json", ".pdb", ".xml"},
                            f"Unexpected application layer file {member.name}.")
                if path == managed_assembly:
                    require(not found and member.isfile(), "The controller DLL must be one regular file.")
                    with layer.extractfile(member) as image_assembly, assembly.open("rb") as tested_assembly:
                        require(digest_stream(image_assembly) == digest_stream(tested_assembly),
                                "The OCI controller assembly differs from the schema-tested build.")
                    found = True
            require(found, "The OCI application layer is missing the controller DLL.")


def verify_image(path, architecture, assembly):
    with tarfile.open(path, mode="r:*") as archive:
        require(read_json(archive, "oci-layout")["imageLayoutVersion"] == "1.0.0", "Expected an OCI image layout.")
        images = read_json(archive, "index.json")["manifests"]
        require(len(images) == 1 and images[0]["mediaType"] == OCI_MANIFEST, "Expected one OCI platform manifest.")
        image = images[0]
        verify_blob(archive, image)
        manifest = read_json(archive, blob_path(image))
        verify_blob(archive, manifest["config"])
        config = read_json(archive, blob_path(manifest["config"]))
        require(config["os"] == "linux" and config["architecture"] == architecture,
                "OCI platform does not match the selected Linux architecture.")
        execution = config["config"]
        require(execution["Labels"]["org.opencontainers.image.base.digest"] == BASE_DIGESTS[architecture],
                "The image selected a different pinned chiseled base.")
        require(execution["User"] == "1654", "The image must run as the chiseled non-root UID.")
        require(execution["WorkingDir"].rstrip("/") == "/app", "Unexpected image working directory.")
        require(execution["Entrypoint"] == ["dotnet", f"/app/{CONTROLLER_DLL}"], "Expected the shellless .NET entry point.")
        require("DOTNET_EnableDiagnostics_IPC=0" in execution["Env"], "The image must not need a diagnostics socket.")
        layers = manifest["layers"]
        require(len(layers) > 1, "Expected a pinned base and a separate application layer.")
        for layer in layers:
            verify_blob(archive, layer)
        verify_application(archive, layers[-1], assembly)
        return image["digest"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("architecture", choices=tuple(BASE_DIGESTS))
    parser.add_argument("--assembly", type=Path, required=True)
    args = parser.parse_args()
    print(verify_image(args.archive, args.architecture, args.assembly))
