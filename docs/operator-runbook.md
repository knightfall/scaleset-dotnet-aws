# Controller operator runbook

Use one AWS account and Region per deployment. Start with the [profile binding template and checks](profile-bindings.md) and record the target evidence in the [qualification checklist](emulator-boundaries.md). The commands under **Prepare the artifacts** can run locally. The ECR, AWS preflight, apply, and destroy commands are for the operator of the qualified target environment.

## Prepare the artifacts

Start with a [verified release archive](releases/VERIFYING.md) or build an
archive from the reviewed source as shown below.
The [maintainer release process](releases/PUBLISHING.md) records how the
archives, image SBOMs and provenance are generated.

1. Check out the reviewed commit. Use the pinned .NET 10 SDK, Node, and npm versions, then install the locked dependencies. Keep the authored YAML, runtime JSON, image archive, and CDK assembly together in the deployment record. Store GitHub credentials and collector headers in the approved Secrets Manager secrets.
2. Copy the binding template into your operator workspace and fill in the AWS and GitHub bindings. Use a dry-run configuration for the first qualification pass. Emit the preliminary runtime and check its schema and digest. Repeat these commands with the final image digest after registry publication.

```sh
make check
npm --prefix cdk ci
mkdir -p "$PWD/operator-staging"

controller_project="$PWD/src/Knightfall.ScaleSet.Aws.Controller/Knightfall.ScaleSet.Aws.Controller.csproj"
source_yaml="$PWD/operator-staging/controller.yaml"
runtime_json="$PWD/operator-staging/runtime.json"
runtime_sha=$(npm --prefix cdk --silent run config:emit -- "$source_yaml" "$runtime_json")
dotnet run --project "$controller_project" --configuration Release --no-build -- \
  config verify --file "$runtime_json" --sha256 "$runtime_sha"
dotnet run --project "$controller_project" --configuration Release --no-build -- \
  reconcile --config "$runtime_json" --sha256 "$runtime_sha" --dry-run --once
```

Run the same Release build and package the CPU architecture selected in the YAML. Use `linux-arm64` with OCI architecture `arm64` for an Arm controller and `linux-x64` with OCI architecture `amd64` for x86-64. The project pins the base image. The archive verifier checks the manifest, architecture, entry point, user, and assembly. [Local OCI verification](controller-local.md#build-the-oci-archives) · [.NET container publishing properties](https://learn.microsoft.com/dotnet/core/containers/publish-configuration)

```sh
rid=linux-arm64
arch=arm64
image_tag="controller-$(git rev-parse --short=12 HEAD)-$rid"
local_image="scaleset-aws-controller:$image_tag"
archive="$PWD/operator-staging/controller-$rid.tar.gz"

dotnet publish "$controller_project" --configuration Release /t:PublishContainer \
  -p:ContainerRuntimeIdentifier="$rid" -p:ContainerImageTag="$image_tag" \
  -p:ContainerArchiveOutputPath="$archive"

python3 tests/oci/verify_controller_image.py "$archive" "$arch" --assembly \
  src/Knightfall.ScaleSet.Aws.Controller/bin/Release/net10.0/Knightfall.ScaleSet.Aws.Controller.dll
```

For a downloaded release, use `download_dir` and `commit` from the verification
steps. Select the architecture configured in your YAML.

```sh
rid=linux-arm64
archive="$download_dir/scaleset-aws-controller-$rid.tar.gz"
local_image="scaleset-aws-controller:git-$commit-$rid"
image_tag="controller-$commit-$rid"
```

## Pin the registry manifest

Use an existing private ECR repository in the configured account and Region and an immutable candidate tag. Verify the caller before any registry write. This example uses the commercial AWS registry suffix. Review the image scan and record the registry manifest digest, architecture, source commit, and corresponding local archive. [ECR image push](https://docs.aws.amazon.com/AmazonECR/latest/userguide/docker-push-ecr-image.html) · [ECR image metadata](https://docs.aws.amazon.com/cli/latest/reference/ecr/describe-images.html)

```sh
account=111122223333
region=ap-southeast-2
repository=scaleset-aws-controller
registry="$account.dkr.ecr.$region.amazonaws.com"

test "$(aws sts get-caller-identity --region "$region" --query Account --output text)" = "$account" || exit 1
aws ecr get-login-password --region "$region" | docker login --username AWS --password-stdin "$registry"
docker image load --input "$archive"
docker image tag "$local_image" "$registry/$repository:$image_tag"
docker image push "$registry/$repository:$image_tag"

registry_digest=$(aws ecr describe-images --region "$region" --registry-id "$account" \
  --repository-name "$repository" --image-ids "imageTag=$image_tag" \
  --query 'imageDetails[0].imageDigest' --output text)
printf '%s\n' "$registry/$repository@$registry_digest"
```

Compare the published manifest with the qualified archive. If registry transport changed the manifest format, confirm the platform, image configuration, and layers of the registry digest before using it. Put the full `registry/repository@sha256:…` URI in `spec.controller.image`, then re-emit and verify the final runtime. The ECR manifest digest identifies the image. The runtime SHA-256 identifies the JSON bytes. CloudFormation reports the S3 object version and generation after apply.

## Review synth and diff

Select a positive generation greater than the deployed generation. Use the same candidate YAML, Fargate preflight JSON, and optional host telemetry settings for both review and apply. The examples assume paths without single quotes. Omit `--telemetry` and its argument to use the defaults. Keep the Fargate approval and fresh quota evidence in the invoking environment when the enabled Fargate profiles require them. [Fargate preflight input](controller-local.md#controller-preflights) · [Telemetry deployment settings](operator-telemetry.md)

```sh
generation=1
assembly="$PWD/operator-staging/cdk.out"
telemetry_json="$PWD/operator-staging/telemetry.json"
app="node bin/controller.ts --config '$source_yaml' --generation $generation --telemetry '$telemetry_json'"

(
  cd cdk
  ./node_modules/.bin/cdk synth Controller --app "$app" --output "$assembly" --strict --no-lookups
  ./node_modules/.bin/cdk diff Controller --app "$assembly" --method template --no-lookups
)
```

Verify the CDK bootstrap resources are approved in the target account and Region. Review any indicated replacement against the deployed resource IDs and the eventual CloudFormation change set. Record the reviewed commit, inputs, diff, and operator decision. [CDK bootstrap](https://docs.aws.amazon.com/cdk/v2/guide/ref-cli-cmd-bootstrap.html) · [CDK template diff](https://docs.aws.amazon.com/cdk/v2/guide/ref-cli-cmd-diff.html)

- [ ] The controller service has desired count one, private `awsvpc` subnets, no public IP, a read-only filesystem, and a 90-second task stop timeout. The deployment settings prevent overlapping service-managed controller tasks during an ordinary rollout. `/healthz` is the task health check.
- [ ] The ECR digest, controller cluster, network, tracking table, runtime object key/version contract, role ARNs, and enabled-provider queue rules match the candidate binding.
- [ ] The task and execution roles have the expected individual responsibilities. Verify exact GitHub secret and runner scope, queue resources, launch template and task definition grants, and collector header secrets and CMK conditions.
- [ ] Imported resources keep the same physical identifiers. Review stack-owned changes, retained data, and shared dependencies before approval. Stage any profile removal or privilege contraction through the retirement sequence.

## Preflight and apply

Run `preflight table` with the operator's read-only AWS identity against the exact candidate runtime and digest. It validates the account, active table, `PK` and `SK`, the `due` index, encryption, enabled `expiresAt` TTL, and PITR. An imported table must pass before activation. If the stack creates the table or host cluster, provision the initial stack in dry-run mode, wait for these resources to be ready, then run both checks before deploying an active generation.

```sh
dotnet run --project "$controller_project" --configuration Release --no-build -- \
  preflight table --config "$runtime_json" --sha256 "$runtime_sha"
```

Run `preflight clock` inside an approved one-off Fargate diagnostic task on the intended controller cluster and platform. Provide the exact JSON as a local read-only file in that task, for example by copying the JSON into a diagnostic image derived from the candidate digest. Preserve the non-root user, private subnet and security groups, and read-only root. Configure the one-off task to run the following command in place of the controller command. The real Fargate metadata endpoint supplies the task, cluster, synchronization status, and error bound. Record the exit code and task ARN and confirm the reference timestamp remains fresh across the qualification window. [Fargate task metadata](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-metadata-endpoint-v4-fargate.html) · [Fargate metadata fields](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-metadata-endpoint-v4-fargate-response.html)

```sh
dotnet /app/Knightfall.ScaleSet.Aws.Controller.dll preflight clock \
  --config /operator/runtime.json --sha256 "<verified-runtime-sha256>"
```

Both preflights should exit zero. A failed or unverified result holds activation. Apply through the protected deployment entry point after reviewing the exact candidate. For an existing stack, it reads the prior version and digest from CloudFormation, verifies the deployment scope, checks retirement before preparing a change set and again before executing it, and waits for CloudFormation to complete. A dry-run first apply and the subsequent active apply use separate increasing generations.

```sh
npm --prefix cdk run deploy:controller -- \
  --config "$source_yaml" --generation "$generation" --telemetry "$telemetry_json"
```

### Verify the deployment

1. Compare `DeploymentIdentity`, `TrackingTableName`, `RuntimeConfigUri`, `RuntimeConfigVersionId`, `RuntimeConfigSha256`, and `DeploymentGeneration` in the stack outputs to the recorded inputs. Confirm that ECS selected the expected task definition, image digest, and healthy task. Check the exact prior task ARN if the deployment replaced a controller.
2. Query `curl --fail-with-body --silent --show-error http://127.0.0.1:8080/readyz` from an approved probe in the controller's network namespace. It reports authority and per-profile readiness. ECS uses `/healthz` for process and supervision liveness. The stock service binds health to loopback and disables ECS Exec, so use fresh profile telemetry and lifecycle evidence for routine deployed readiness.
3. Run the receiver smoke check from the intended network, then confirm queryable metrics, traces, and logs for the new `service.instance.id`. Confirm that enabled profiles have fresh readiness and that the resource and profile attributes match the artifact. Investigate zero readiness or absent data with the [telemetry playbook](operator-telemetry.md).
4. Record activation time, exact owner task ARN and lease epoch, runtime version and generation, task definition ARN, image digest, and the qualification evidence for each profile. Retain the previous compatible YAML, runtime, image digest, and assembly.

## Add, drain, and retire a profile

1. Add a distinct profile binding to YAML at a new generation. Preserve existing profile IDs, scope, grants, and resource bindings. Review its labels, group, capacity, and backend before running the protected deploy. Check readiness for the new and existing profiles.
2. Set the selected profile to `draining` in a new generation, with the same GitHub and AWS binding. This stops admissions and launches for that profile while retaining permissions for its existing work. Allow running jobs and their cleanup to finish. Preserve all requests with unknown outcomes for exact reconciliation.
3. Keep the draining generation deployed through two fresh clear inventory passes, closure hold, confirmed GitHub session retirement, and a durable catalog tombstone. Verify each against the current fenced owner. Elapsed time, an empty queue, and an API error each require further evidence when durable records still show ambiguous work. [Lifecycle and retirement](implementation-plan.md#lifecycle-and-retirement)
4. Emit the candidate removal at a greater generation and retrieve the exact current runtime using the stack's URI, version, and digest. Run the read-only check below. A nonzero exit leaves the current deployment and its grants in place. The protected deploy repeats the check at its execution gates. Keep at least one active or draining profile in the YAML, including when switching the entire deployment to dry-run.

```sh
dotnet run --project "$controller_project" --configuration Release --no-build -- \
  preflight retire --current-config "$current_runtime_json" --current-sha256 "$current_runtime_sha" \
  --current-generation "$current_generation" --config "$runtime_json" --sha256 "$runtime_sha" \
  --generation "$generation"
```

## Roll back or recover a controller

Roll back by deploying compatible known-good inputs as a **new, higher generation**. Compare current catalog and profile grants against the candidate before the deploy. Keep intervening profiles and bindings until their own retirement gates clear. Preserve tombstones and choose a new profile identity if the old profile is retired. Verify the writer protocol, data schema, CPU architecture, image digest, and runtime bytes before starting the replacement.

On an abrupt exit, record the exact predecessor task ARN, current lease epoch, unresolved effect or acknowledgement IDs, and latest authorized attempt times. Allow the successor to acquire recovery-only authority, verify the exact predecessor is `STOPPED` in the configured cluster, use the trusted clock bound, and settle effects by authoritative readback. Use a verified clean handover before moving the controller cluster. If an unclean move leaves the predecessor outside the new role's exact lookup scope, hold activation and use a separately reviewed isolation and recovery procedure. Keep ambiguous acknowledgements and runner registrations held. A new session opens with a fresh cursor after confirmed retirement of the exact prior session. [Recovery and target boundaries](emulator-boundaries.md)

### Recover from a DynamoDB backup

1. Stop or isolate the prior controller and close bootstrap access to the destination. Preserve the source table, its effect journal, and external inventory as evidence. Select the backup or PITR point and restore to a separate table with the required keys, index, and encryption. Reapply TTL, PITR, and narrowly scoped access on the restored table. [DynamoDB restore considerations](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/pointintimerecovery_restores.html)
2. With a dedicated recovery identity, scan every page of the restored table using consistent reads and purge the restored `HELLO#`, `OFFER#`, and untrusted `STATUS#` bootstrap records. Re-scan to verify the purge. Keep lease, catalog, checkpoint, and operation records available for reconciliation.
3. Compare outstanding operations to the authoritative EC2, ECS, and GitHub observations and available source evidence. A post-backup side effect may already exist, and a restored `Planned` record alone cannot establish whether a send occurred after the backup. Hold those effects until their outcomes are resolved. Verify the table and establish fenced ownership before resuming admissions.
4. The protected deploy requires the existing deployment to keep the same tracking table. An initial import also requires an empty catalog for the deployment. Keep service and bootstrap access stopped when the wrapper rejects a restored table. Use a reviewed recovery cutover that explicitly binds the verified destination and scoped identities after reconciliation. Confirm stale runner credentials remain denied before issuing fresh target-scoped access.

## Decommission with CDK

- [ ] Drain all owned profiles and verify all resulting tombstones and exact session closures. Confirm active or unknown jobs, unresolved operations, and unclaimed runner resources have authoritative outcomes. Leave the deployment in a nonadmitting state while resolving uncertain work.
- [ ] Deploy a higher-generation dry-run configuration after retirement, then verify the active controller task has stopped and no profile can start or claim work.
- [ ] Record the AWS account, Region, physical stack ID, cluster, table, versioned runtime location, and approved retained resources. Inspect the exact deployed assembly and shared imports. Confirm the intended retention policy for archives and CloudWatch logs.
- [ ] Run destroy only for the reviewed stack ID. Verify CloudFormation completion, imported resources intact, and no remaining controller task or enabled producer rule. The stack retains its created table, runtime bucket, and log group. Remove retained data or shared resources only under their separate retention procedures. [CDK destroy and retention](https://docs.aws.amazon.com/cdk/v2/guide/ref-cli-cmd-destroy.html)

```sh
(
  cd cdk
  ./node_modules/.bin/cdk destroy Controller --app "$deployed_assembly" --exclusively
)
```
