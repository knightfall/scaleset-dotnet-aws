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
docker run --rm --pull=never --platform "$platform" --network none --read-only \
  --mount "type=bind,source=$PWD/$test_build,target=/tests,readonly" \
  --env TESTINGPLATFORM_TELEMETRY_OPTOUT=1 \
  --env SCALESET_HEALTHCHECK_ASSEMBLY=/app/Knightfall.ScaleSet.Aws.Controller.dll \
  --entrypoint dotnet "scaleset-aws-controller:$tag" \
  /tests/Knightfall.ScaleSet.Aws.Core.Tests.dll \
  --filter-class '*ControllerHealthTests' --results-directory /tmp
