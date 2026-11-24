# Local controller verification

Use the pinned .NET SDK to verify a compiled runtime configuration. The command prints the SHA-256 of the verified bytes and uses only the local file.

```sh
dotnet run --project src/Knightfall.ScaleSet.Aws.Controller -- \
  config verify --file /tmp/runner-runtime.json --sha256 "$runtime_sha256"
```

An advisory one-shot reconciliation works with either local or version-pinned S3 input. It uses the read-only graph even when the input document specifies active mode.

```sh
dotnet run --project src/Knightfall.ScaleSet.Aws.Controller -- \
  reconcile --config /tmp/runner-runtime.json --dry-run --once

dotnet run --project src/Knightfall.ScaleSet.Aws.Controller -- \
  reconcile --config-s3-uri "$runtime_s3_uri" --config-version-id "$runtime_version" \
  --sha256 "$runtime_sha256" --dry-run --once
```

For S3 runtime input, configure the AWS SDK region and credentials for the configuration bucket. S3 reads require both the supplied version and the expected bytes to match. Configuration and demand reads from S3 each support artifacts up to 64 MiB. A local demand snapshot or replay uses the local runtime file's directory to resolve relative paths. A remote demand snapshot uses its own configured S3 version and digest.

The long-running controller verifies its runtime digest and requires a positive configuration generation.

```sh
dotnet run --project src/Knightfall.ScaleSet.Aws.Controller -- \
  run --config /tmp/runner-runtime.json --sha256 "$runtime_sha256" --generation 1
```

Use `--config-s3-uri` and `--config-version-id` in place of `--config` to use a versioned S3 runtime. Event queue URLs are optional and each requires its corresponding enabled provider in active mode. A runtime configured for dry-run stays in a read-only supervision loop and reports readiness without a lease. On SIGTERM or SIGINT, the controller stops admissions and polls immediately and uses one 60-second process shutdown budget, including health listener shutdown and up to five seconds for telemetry.

The controller binds health to `127.0.0.1:8080` by default. The `healthcheck` command calls `/healthz`, which reports whether process and supervision are progressing. `/readyz` reports verified configuration, lease state where ownership is required, and readiness for each profile. Use the same `--health-addr` on the controller and probe when overriding the default.

## Build the OCI archives

From the repository root, set an absolute output path for each invocation. The SDK downloads the pinned Microsoft .NET 10 ASP.NET Core Noble chiseled-extra base and writes an OCI archive locally. [Microsoft container publishing configuration](https://learn.microsoft.com/en-us/dotnet/core/containers/publish-configuration)

```sh
project=src/Knightfall.ScaleSet.Aws.Controller/Knightfall.ScaleSet.Aws.Controller.csproj
assembly=src/Knightfall.ScaleSet.Aws.Controller/bin/Release/net10.0/Knightfall.ScaleSet.Aws.Controller.dll

dotnet test --project tests/Knightfall.ScaleSet.Aws.Core.Tests/Knightfall.ScaleSet.Aws.Core.Tests.csproj --configuration Release

dotnet publish "$project" --configuration Release /t:PublishContainer \
  -p:ContainerRuntimeIdentifier=linux-x64 -p:ContainerImageTag=linux-x64 \
  -p:ContainerArchiveOutputPath=/tmp/scaleset-controller-linux-x64.tar.gz
python3 tests/oci/verify_controller_image.py /tmp/scaleset-controller-linux-x64.tar.gz amd64 --assembly "$assembly"

dotnet publish "$project" --configuration Release /t:PublishContainer \
  -p:ContainerRuntimeIdentifier=linux-arm64 -p:ContainerImageTag=linux-arm64 \
  -p:ContainerArchiveOutputPath=/tmp/scaleset-controller-linux-arm64.tar.gz
python3 tests/oci/verify_controller_image.py /tmp/scaleset-controller-linux-arm64.tar.gz arm64 --assembly "$assembly"
```

The verifier prints the architecture-specific OCI manifest digest. It also checks the non-root UID, shellless entry point, and the application assembly against the local build. The tests compare the embedded schema resources with their canonical source files. The image uses `dotnet /app/Knightfall.ScaleSet.Aws.Controller.dll healthcheck` for the probe, and ECS enforces the read-only root filesystem at runtime.

To exercise the packaged health command with a local Linux engine, load the archive and run the same acceptance tests with a read-only root filesystem. The tests start a loopback listener, then invoke the controller assembly packaged in the image. Set `rid=linux-x64` and `platform=linux/amd64` to repeat the test for x64.

```sh
rid=linux-arm64
platform=linux/arm64
test_build="$PWD/tests/Knightfall.ScaleSet.Aws.Core.Tests/bin/Release/net10.0"
docker image load --input "/tmp/scaleset-controller-$rid.tar.gz"
docker run --rm --pull=never --platform "$platform" --network none --read-only \
  --mount "type=bind,source=$test_build,target=/tests,readonly" \
  --env TESTINGPLATFORM_TELEMETRY_OPTOUT=1 \
  --env SCALESET_HEALTHCHECK_ASSEMBLY=/app/Knightfall.ScaleSet.Aws.Controller.dll \
  --entrypoint dotnet "scaleset-aws-controller:$rid" \
  /tests/Knightfall.ScaleSet.Aws.Core.Tests.dll \
  --filter-class '*ControllerHealthTests' --results-directory /tmp
```
