#!/usr/bin/env bash
set -euo pipefail

rid=$1
output=$(mkdir -p "$2" && cd "$2" && pwd)
case "$rid" in
  linux-x64) platform=linux/amd64 ;;
  linux-arm64) platform=linux/arm64 ;;
  *) echo "Expected linux-x64 or linux-arm64." >&2; exit 2 ;;
esac

project=src/Knightfall.ScaleSet.Aws.Controller/Knightfall.ScaleSet.Aws.Controller.csproj
test_project=tests/Knightfall.ScaleSet.Aws.Core.Tests/Knightfall.ScaleSet.Aws.Core.Tests.csproj
test_build="$(dirname "$test_project")/bin/Release/net10.0"
assembly="$(dirname "$project")/bin/Release/net10.0/Knightfall.ScaleSet.Aws.Controller.dll"
archive="$output/scaleset-aws-controller-$rid.tar.gz"
tag="git-$(git rev-parse HEAD)-$rid"

dotnet restore "$test_project" --locked-mode
dotnet build "$test_project" --configuration Release --no-restore
dotnet test --project "$test_project" --configuration Release --no-build
dotnet publish "$project" --configuration Release --no-restore /t:PublishContainer \
  -p:ContainerRuntimeIdentifier="$rid" -p:ContainerImageTag="$tag" \
  -p:ContainerArchiveOutputPath="$archive"
python3 tests/oci/verify_controller_image.py "$archive" "${platform#linux/}" --assembly "$assembly"

docker image load --input "$archive"
runtime_fixture="$PWD/testdata/config/runtime/ec2-only-pinned.json"
runtime_digest=$(sha256sum "$runtime_fixture" | cut -d ' ' -f 1)
run_controller() {
  docker run --rm --pull=never --platform "$platform" --network none --read-only \
    --env ECS_CONTAINER_METADATA_URI_V4= \
    --mount "type=bind,source=$runtime_fixture,target=/runtime.json,readonly" \
    "scaleset-aws-controller:$tag" "$@"
}
test "$(run_controller config verify --file /runtime.json --sha256 "$runtime_digest")" = "$runtime_digest"
clock_exit=0
clock_output=$(run_controller preflight clock --config /runtime.json --sha256 "$runtime_digest" 2>&1) || clock_exit=$?
if [[ "$clock_exit" != 1 || "$clock_output" != 'Controller clock preflight could not verify a fresh synchronized ECS task clock.' ]]; then
  echo "The packaged clock preflight did not accept the ECS command arguments and fail closed without metadata." >&2
  exit 1
fi
docker run --rm --pull=never --platform "$platform" --network none --read-only \
  --mount "type=bind,source=$PWD/$test_build,target=/tests,readonly" \
  --env TESTINGPLATFORM_TELEMETRY_OPTOUT=1 \
  --env SCALESET_HEALTHCHECK_ASSEMBLY=/app/Knightfall.ScaleSet.Aws.Controller.dll \
  --entrypoint dotnet "scaleset-aws-controller:$tag" \
  /tests/Knightfall.ScaleSet.Aws.Core.Tests.dll \
  --filter-class '*ControllerHealthTests' --results-directory /tmp
