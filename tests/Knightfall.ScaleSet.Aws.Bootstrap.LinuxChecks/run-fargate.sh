#!/bin/sh
set -eu

fixture=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo=$(CDPATH= cd -- "$fixture/../.." && pwd)
project="$fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.csproj"
runner_image=ghcr.io/actions/actions-runner@sha256:6d0b2d80d80f143f77dcb676e50920100bbb547e39e883454d57f45ab8c31300
dotnet_image=mcr.microsoft.com/dotnet/sdk@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4

dotnet restore "$project" --locked-mode
dotnet build "$project" --configuration Release --no-restore

context=$(mktemp -d)
trap 'rm -rf "$context"' EXIT HUP INT TERM
cp "$repo/src/Knightfall.ScaleSet.Aws.RunnerBootstrap/Fargate/runner-child.sh" "$context/runner-child"
cp "$fixture/fargate-bootstrap.json" "$context/fargate-bootstrap.json"
cat > "$context/fixture-dotnet" <<'FIXTURE'
#!/bin/sh
set -eu

test "$(id -u)" = 1001
test "$#" = 4
test "$1" = exec
test "$2" = "$SCALESET_RUNNER_ROOT/bin/Runner.Listener.dll"
test "$3" = run
test "$4" = --once
test -z "${AWS_SESSION_TOKEN:-}"
test -z "${ACTIONS_RUNNER_INPUT_JITCONFIG:-}"
grep -Eq '^NoNewPrivs:[[:space:]]+1$' "/proc/$$/status"
grep -Eq '^CapEff:[[:space:]]+0+$' /proc/self/status
test "$(stat -c %a .credentials)" = 600
test "$(stat -f -c %T .credentials)" = tmpfs
test "$(cat .credentials)" = offline-fargate-jit-sentinel
test "$(stat -f -c %T "$SCALESET_RUNNER_ROOT")" = tmpfs
mkdir -p home/job/nested
printf 'fixture\n' > home/job/nested/output
chmod 000 home/job/nested
chmod 500 home/job
if [ "${0##*/}" = fixture-dotnet-stop ]; then
    trap 'echo signal > _work/receipt; exit 0' TERM
    printf '2000-01-01 00:00:00Z: Listening for Jobs\n'
    while :; do sleep 1; done
fi
printf 'offline-fargate-jit-sentinel\n2000-01-01 00:00:00Z: Listening for Jobs\n'
echo completed > _work/receipt
FIXTURE
chmod 755 "$context/runner-child" "$context/fixture-dotnet"

cat > "$context/Dockerfile" <<'DOCKERFILE'
ARG RUNNER_IMAGE
ARG DOTNET_IMAGE
FROM ${DOTNET_IMAGE} AS runtime
FROM ${RUNNER_IMAGE} AS runner
FROM ${RUNNER_IMAGE}
COPY --from=runtime /usr/share/dotnet/dotnet /opt/scaleset/bootstrap-dotnet/dotnet
COPY --from=runtime /usr/share/dotnet/host /opt/scaleset/bootstrap-dotnet/host
COPY --from=runtime /usr/share/dotnet/shared/Microsoft.NETCore.App /opt/scaleset/bootstrap-dotnet/shared/Microsoft.NETCore.App
COPY --from=runtime /usr/share/dotnet/dotnet /opt/scaleset/runner-dotnet/dotnet
COPY --from=runner /home/runner/bin/libhostfxr.so /opt/scaleset/runner-dotnet/host/fxr/8.0.0/libhostfxr.so
COPY --from=runner --chown=0:0 /home/runner/bin /opt/scaleset/runner/bin
COPY --from=runner --chown=0:0 /home/runner/externals /opt/scaleset/runner/externals
COPY fargate-bootstrap.json /opt/scaleset/fargate-bootstrap.json
COPY runner-child /opt/scaleset/runner-child
COPY fixture-dotnet /opt/scaleset/fixture-dotnet
COPY fixture-dotnet /opt/scaleset/fixture-dotnet-stop
DOCKERFILE

image=$(DOCKER_BUILDKIT=0 docker build --quiet --network=none --pull=false \
    --build-arg "RUNNER_IMAGE=$runner_image" --build-arg "DOTNET_IMAGE=$dotnet_image" "$context")
run_fixture() {
    docker run --rm --pull never --network none --read-only --user 1001:1001 \
        --cap-drop ALL --memory=768m --memory-swap=768m \
        --tmpfs /work:rw,nosuid,nodev,size=256m,mode=1777 \
        --mount "type=bind,source=$fixture/bin/Release/net10.0,target=/fixture,readonly" \
        --entrypoint /opt/scaleset/bootstrap-dotnet/dotnet "$@"
}
output=$(run_fixture "$image" \
    /fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.dll fargate)

if rejection=$(run_fixture --init "$image" /fixture/Knightfall.ScaleSet.Aws.RunnerBootstrap.dll \
    fargate /fixture/unused.json 2>&1) || [ "$rejection" != "Fargate bootstrap must run as PID 1." ]; then
    printf '%s\n' "$rejection" >&2
    exit 1
fi

rejection=$(run_fixture "$image" /fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.dll fargate-production-metadata 2>&1)
[ "$rejection" = "Fargate bootstrap failed." ]

mkdir "$context/locked"
run_fixture --ulimit nofile=96:96 --tmpfs /cleanup:rw,uid=1001,gid=1001,mode=0700 \
    --mount "type=bind,source=$context/locked,target=/cleanup/.runner,readonly" \
    "$image" /fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.dll fargate-cleanup
# PID 1 must exit with an incomplete cleanup while its private task still contains the 60-second fixture child.
started=$(date +%s)
status=0
incomplete=$(run_fixture "$image" /fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.dll fargate-incomplete-cleanup 2>&1) || status=$?
[ "$status" -eq 1 ]
[ "$incomplete" = "Incomplete cleanup while another process was present." ]
[ "$(( $(date +%s) - started ))" -lt 30 ]
run_fixture --user 1001:0 "$image" /fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.dll fargate-rejected-identity
run_fixture --group-add 0 "$image" /fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.dll fargate-rejected-identity
run_fixture --group-add 999 "$image" /fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.dll fargate-rejected-identity
run_fixture --cap-add NET_BIND_SERVICE "$image" /fixture/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks.dll fargate-rejected-identity

case "$output" in
    *offline-fargate-jit-sentinel*|*offline-parent-credential-sentinel*|*offline-parent-jit-sentinel*) exit 1 ;;
esac
printf '%s\n' "$output"
