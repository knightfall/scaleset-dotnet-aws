#!/bin/sh
set -eu

fixture=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
project="$fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.csproj"
image=${SCALESET_LINUX_FIXTURE_IMAGE:-mcr.microsoft.com/dotnet/sdk@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4}

dotnet restore "$project" --locked-mode
dotnet build "$project" --configuration Release --no-restore

docker run --rm --pull never --network none --user 0:0 \
    --memory=768m --memory-swap=768m --cap-add SYS_ADMIN \
    --mount "type=bind,source=$fixture/bin/Release/net10.0,target=/fixture,readonly" \
    --entrypoint /bin/sh "$image" -c '
        mkdir /run/scaleset-fixture
        mount -t tmpfs -o rw,exec,nosuid,nodev,noswap,mode=1770,gid=1500 tmpfs /run/scaleset-fixture
        ulimit -c 0
        exec dotnet /fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.dll
    '
