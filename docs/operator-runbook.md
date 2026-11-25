# Controller operator runbook

Use one AWS account and Region per deployment. Start with the [profile binding template and checks](profile-bindings.md) and record the target evidence in the [qualification checklist](emulator-boundaries.md). The commands under **Prepare the artifacts** can run locally. The ECR, AWS preflight, apply, and destroy commands are for the operator of the qualified target environment.

## Prepare the artifacts

The default Direct EC2 mode holds a still-running host after GitHub reports job
completion. Verify process exit and log collection before reclaiming it through
your approved external procedure. For automatic cleanup, bind the qualified AMI
and Launch Template to the supervisor's status grant and boot proof. [EC2 cleanup qualification](profile-bindings.md#qualify-ec2-supervisor-cleanup)

Start with a [verified release archive](releases/VERIFYING.md) or build an
archive from the reviewed source as shown below.
The [maintainer release process](releases/PUBLISHING.md) records how the
archives, image SBOMs and provenance are generated.

1. Check out the reviewed commit. Use the pinned .NET 10 SDK, Node, and npm versions, then install the locked dependencies. Keep the authored YAML, runtime JSON, image archive, and CDK assembly together in the deployment record. Store GitHub credentials and collector headers in the approved Secrets Manager secrets.
2. Copy the binding template into your operator workspace and fill in the AWS and GitHub bindings. Use a dry-run configuration for the first qualification pass. Emit the preliminary runtime and check its schema and digest. Repeat these commands with the final image digest after registry publication.

```sh
make check
npm --prefix cdk ci --engine-strict --ignore-scripts
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

For a downloaded release, complete the [attested manifest and payload checks](releases/VERIFYING.md#verify-the-published-manifest-and-payloads)
before importing any image. Use `download_dir` and `commit` from that verified
release. Select the architecture configured in your YAML.

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

Select a positive generation greater than the deployed generation. Use the same candidate YAML, Fargate preflight JSON, and optional host telemetry settings for both review and apply. The examples assume paths without single quotes. Omit `--telemetry` and its argument to use the defaults. Include the Fargate approvals and the designated quota source when enabled Fargate profiles require them. Verify that the source has a current sample before activation. [Fargate preflight input](controller-local.md#controller-preflights) · [Telemetry deployment settings](operator-telemetry.md)

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

Verify the CDK bootstrap resources are approved in the target account and Region. For customer-managed encryption, supply the same reviewed `--encryption <path>` input to synth and the protected deployment. Review any indicated replacement against the deployed resource IDs and the eventual CloudFormation change set. Record the reviewed commit, inputs, diff, and operator decision. [Imported encryption prerequisites](profile-bindings.md#qualify-imported-encryption-keys) · [CDK bootstrap](https://docs.aws.amazon.com/cdk/v2/guide/ref-cli-cmd-bootstrap.html) · [CDK template diff](https://docs.aws.amazon.com/cdk/v2/guide/ref-cli-cmd-diff.html)

- [ ] The controller service has desired count one, private `awsvpc` subnets, no public IP, a read-only filesystem, and a 90-second task stop timeout. The deployment settings prevent overlapping service-managed controller tasks during an ordinary rollout. `/healthz` is the task health check.
- [ ] The ECR digest, controller cluster, network, tracking table, runtime object key/version contract, role ARNs, and enabled-provider queue rules match the candidate binding.
- [ ] The task and execution roles have the expected individual responsibilities. Verify exact GitHub secret and runner scope, queue resources, launch template and task definition grants, and collector header secrets and CMK conditions.
- [ ] Imported resources keep the same physical identifiers. Review stack-owned changes, retained data, and shared dependencies before approval. Stage any profile removal or privilege contraction through the retirement sequence.

## Preflight and apply

Start with offline verification and the local emulated matrix. If a separate AWS test deployment is authorized, select `spec.mode: dry-run` for the first apply. Leave `inspectAws` off for the offline planner behavior. Turn AWS inspection on only for an approved target with `dryRun.demand.type: none`. The dry-run task has no GitHub listener, lifecycle table or event consumer grants. Active and draining profiles may still perform lifecycle work when deployed in `spec.mode: active`. Set that mode in the reviewed YAML only after approving the target evidence. The deploy command takes the YAML and a monotonically increasing generation. [Deployment modes and inventory](controller-local.md) · [Target qualification gates](emulator-boundaries.md) · [Role separation](profile-bindings.md#secrets-and-role-separation) · [Imported encryption](profile-bindings.md#qualify-imported-encryption-keys)

Run `preflight table` with the operator's read-only AWS identity against the exact candidate runtime and digest. It validates the account, active table, `PK` and `SK`, the `due` index, encryption, enabled `expiresAt` TTL, and PITR. An imported table must pass before activation. If the stack creates the table or host cluster, provision the initial stack in dry-run mode, wait for these resources to be ready, then run both checks before deploying an active generation.

```sh
dotnet run --project "$controller_project" --configuration Release --no-build -- \
  preflight table --config "$runtime_json" --sha256 "$runtime_sha"
```

Run `preflight clock` inside an approved one-off Fargate diagnostic task on the intended controller cluster and platform. Provide the exact JSON as a local read-only file in that task, for example by copying the JSON into a diagnostic image derived from the candidate digest. Preserve the non-root user, private subnet and security groups, and read-only root. Keep the image entrypoint `dotnet /app/Knightfall.ScaleSet.Aws.Controller.dll` and set only the following ECS `command` array. The Fargate metadata endpoint supplies the task, cluster, synchronization status, and error bound. Record the exit code and task ARN and confirm the reference timestamp remains fresh across the qualification window. [ECS task command and entrypoint](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html) · [Fargate metadata fields](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-metadata-endpoint-v4-fargate-response.html)

```json
["preflight", "clock", "--config", "/operator/runtime.json", "--sha256", "<verified-runtime-sha256>"]
```

Both preflights should exit zero. A failed or unverified result holds activation. Native image jobs check that the command reaches the packaged clock preflight and fails closed without ECS metadata. Record the live one-off task result separately. Run `preflight table` and `preflight clock` as operator checks before calling the deploy wrapper. For an existing stack, the wrapper reads the prior version and digest from CloudFormation, verifies deployment scope, checks retirement before preparing a change set and again before executing it, and waits for CloudFormation to complete. An initial dry-run apply and the subsequent active apply use separate increasing generations. [Retirement checks](#add-drain-and-retire-a-profile) · [Predecessor fencing](#roll-back-or-recover-a-controller)

```sh
npm --prefix cdk run deploy:controller -- \
  --config "$source_yaml" --generation "$generation" --telemetry "$telemetry_json"
```

### Recover an interrupted apply

Use AWS CLI v2 and the same reviewed YAML, generation, and optional deployment inputs for recovery. Deployment names start with an ASCII letter and contain at most 110 letters, digits or hyphens so the derived CloudFormation name fits its 128-character limit. Deploy, synth and runtime emission all require valid UTF-8. [CloudFormation stack names](https://docs.aws.amazon.com/AWSCloudFormation/latest/APIReference/API_CreateStack.html) · [AWS CLI return codes](https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-returncodes.html)

Before preparing a change set, the command writes a private recovery record under `~/.local/state/scaleset-dotnet-aws/deployments`. It identifies the reviewed runtime and synthesized template, the previously observed stack revision, and the attempted change set. A failed command reports the record path. After an abrupt stop, inspect the records in that directory and select the matching deployment identity, generation and digest. Resume one matching operation at a time. Include the original `--encryption` option if the deployment used one.

```sh
npm --prefix cdk run deploy:controller -- \
  --config "$source_yaml" --generation "$generation" --telemetry "$telemetry_json" \
  --resume "$recovery_record"
```

The command checks the exact CloudFormation stack and change set IDs and reads the proposed template back before execution. It reruns retirement or fresh-table checks and imported-key verification immediately before executing. A denied check can discard only the verified unexecuted change set. A retry can reuse its exact `REVIEW_IN_PROGRESS` stack. A lost execution reply leaves the record for reconciliation. Recovery uses the original execute token, and success requires the exact deployed template, generation and versioned runtime after the waiter. CloudFormation may invalidate other pending change sets once one update executes. [CloudFormation change set execution](https://docs.aws.amazon.com/AWSCloudFormation/latest/APIReference/API_ExecuteChangeSet.html)

An unmatched review stack or replacement stack holds the operation. For an orphan without its recovery record, coordinate all writers before recovery. Use the exact stack ARN to inspect its state, all change sets and provisioned resources, and correlate the original operation in CloudTrail. Keep the stack on hold if ownership, emptiness or exclusive control is uncertain. Handle any separately authorized empty-stack removal through CloudFormation using the verified ARN. Validate the provider's review, cleanup and rollback behavior in the intended account and Region before relying on recovery in production. [CloudFormation stack review](https://docs.aws.amazon.com/AWSCloudFormation/latest/APIReference/API_CreateChangeSet.html) · [CloudFormation change set deletion](https://docs.aws.amazon.com/AWSCloudFormation/latest/APIReference/API_DeleteChangeSet.html)

### Verify the deployment

1. Compare `DeploymentIdentity`, `TrackingTableName`, `RuntimeConfigUri`, `RuntimeConfigVersionId`, `RuntimeConfigSha256`, and `DeploymentGeneration` in the stack outputs to the recorded inputs. Confirm that ECS selected the expected task definition, image digest, and healthy task. Check the exact prior task ARN if the deployment replaced a controller.
2. Query `curl --fail-with-body --silent --show-error http://127.0.0.1:8080/readyz` from an approved probe in the controller's network namespace. It reports authority and per-profile readiness. ECS uses `/healthz` for process and supervision liveness. The stock service binds health to loopback and disables ECS Exec, so use fresh profile telemetry and lifecycle evidence for routine deployed readiness.
3. Run the receiver smoke check from the intended network, then confirm queryable metrics, traces, and logs for the new `service.instance.id`. Confirm that enabled profiles have fresh readiness and that the resource and profile attributes match the artifact. Investigate zero readiness or absent data with the [telemetry playbook](operator-telemetry.md).
4. Record activation time, exact owner task ARN and lease epoch, runtime version and generation, task definition ARN, image digest, and the qualification evidence for each profile. Retain the previous compatible YAML, runtime, image digest, and assembly.

The stack does not provision CloudWatch alarms. Configure and qualify operator-owned alerts for controller authority and readiness, queue age and DLQ depth, and broker errors and throttling. The ECS controller log group, runtime-artifact Lambda log group, and role-specific HELLO broker log groups retain entries for 30 days and survive stack removal according to CloudFormation retention. Log groups created implicitly by a prior version can retain their old settings. Review their retention separately. [Operator telemetry alert guidance](operator-telemetry.md)

An active profile with `listenerFailure` in `/readyz` remains unready while its listener recovers. `/healthz` continues to report process liveness, and other profiles can keep serving work. For `listener_storage_rejected`, verify the controller role's access to the deployed tracking table. The listener retries with delays capped at 30 seconds, rechecks its catalog binding and lease, and resumes its retained client and checkpoint. Readiness resumes after a successful queue poll with the resulting checkpoint work settled.

A recovering listener with no retained client checks the current credential before opening a session once the durable record is absent or confirms exact retirement. An uncertain open or close stays held. With a retained client, a definitive queue GET HTTP 401 can trigger credential replacement once the checkpoint is settled. The current secret version must resolve to the same runner group, scale set and labels. For `listener_credential_rejected`, verify the current secret and that binding. The controller journals one close of the retained session under the current owner, then requires confirmation and durable retirement before opening a fresh session and cursor. An HTTP 401 from the SDK's session refresh or a message acknowledgement, an unconfirmed session mutation, a checkpoint conflict or repeated unexpected failures can leave `listener_recovery_required`. Inspect the exact session and checkpoint before cycling the controller or attempting session retirement. Preserve the pending acknowledgement and cursor evidence. Qualify how the intended GitHub credential type and rotation procedure behave on the queue and administration endpoints before relying on this recovery. [Session recovery contract](dependency-baseline.md#session-recovery-and-qualification-boundary)

### Qualify encrypted observation delivery

For each enabled backend, record the exact EventBridge rules, primary and dead-letter queues, and their shared KMS key. Each queue permits `sqs:SendMessage` from EventBridge only for its controller rules. The provider key permits the EventBridge encryption actions for the deploying account. The controller consumes the primary queue. The same dead-letter queue handles EventBridge delivery failures and SQS redrive after the configured receive limit. [EventBridge queue policy](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-use-resource-based.html#sqs-permissions) · [EventBridge dead-letter policy](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-rule-dlq.html) · [SQS key management](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-key-management.html)

Both consumers leave malformed envelopes and account or Region mismatches unacknowledged. SQS applies the source queue's receive limit of five. The DLQ has a 14-day retention setting, measured from the original enqueue time for standard SQS redrive. The controller does not make a provider read or a lifecycle decision from a malformed envelope. A well-formed Fargate hint outside the configured cluster or task-definition bindings may be acknowledged as irrelevant, subject to the current lease. For matching resources, incomplete readback or unresolved durable writes leave the delivery for retry. Successful readback and current authority permit deleting the exact receipt. Inspect DLQ metadata while keeping message bodies out of logs and support posts. [SQS redrive and retention](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html)

In an authorized qualification stack, observe a matching EC2 or ECS state change arrive on the encrypted primary queue and reach the controller. Use a controlled target-delivery failure to verify EventBridge can write the encrypted dead-letter queue. Exercise SQS receive-limit redrive separately and confirm the operator can read the retained messages. Record the regional delivery result, including any key or queue access errors. Offline CDK synthesis verifies policy scope and routing. Actual service context and cross-account behavior require target evidence.

## Cut over runner HELLO registration

The controller stack reports a `Bootstrap…HelloUrl` output for each enabled
bootstrap role. The output description identifies the matching role ARN. A
dry-run deployment provides stable URLs with the brokers disabled. The active
stack grants each role its own AWS IAM Function URL invocation and
principal-scoped OFFER consumption. Each broker reserves the sum of `maxRunners`
across its role's active and draining profiles in the stack, capped at 16 concurrent invocations.
Profiles sharing a role also share that reservation. Disabled profiles contribute
zero and dry-run brokers reserve zero.
Active runner deployment requires a commercial AWS Region with Function URLs.
GovCloud and China configurations remain available for dry-run without the broker.
Runner subnets need an approved outbound Internet route for HTTPS to the role's exact endpoint.
[Function URL regional availability and networking](https://docs.aws.amazon.com/lambda/latest/dg/urls-configuration.html) · [GovCloud service differences](https://docs.aws.amazon.com/govcloud-us/latest/UserGuide/govcloud-lambda.html) · [China service differences](https://docs.amazonaws.cn/en_us/aws/latest/userguide/lambda.html)

Before applying an active generation, compare the sum of its broker reservations
and existing reserved concurrency with the account's Lambda concurrency quota
in that Region. Lambda keeps at least 100 units unreserved. A rejected reservation
blocks the stack update. Reservations cap simultaneous invocations and AWS also
enforces synchronous request-rate quotas. Use Lambda `ConcurrentExecutions`,
`Throttles`, `Invocations`, and `Duration` to qualify the expected startup
burst and check its downstream DynamoDB load. Local fixtures exercise throttling,
lost responses, and conditional writes. Target throughput, cold starts, and
account-specific quotas require live qualification. [Lambda reserved concurrency](https://docs.aws.amazon.com/lambda/latest/dg/configuration-concurrency.html) · [Lambda invocation limits](https://docs.aws.amazon.com/lambda/latest/dg/lambda-concurrency.html)

The broker takes the principal from AWS authentication, validates up to 1 KiB
of HELLO input, and conditionally creates one item at
`PK=HELLO#BROKER#<aws:userid>` and `SK=BOOTSTRAP` per principal.
JIT staging and begin require that exact broker record. Legacy
runner grants cannot address the broker's partition namespace.
The broker sets `expiresAt` one hour after registration. Exact retries keep the
same item and expiry.
The controller rejects expired registrations immediately, and DynamoDB removes
the physical record asynchronously. The active bootstrap role denies direct
HELLO changes on the lifecycle table. [Lambda Function URL authentication](https://docs.aws.amazon.com/lambda/latest/dg/urls-auth.html) · [DynamoDB TTL](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/TTL.html)

On throttling, transient HTTP failures, or a lost reply, bootstrap re-signs the
request with the original HELLO body. Delay starts at 125–250 ms and grows to a jittered 2–4 s.
The client allows up to seven sends in a single 25-second registration budget.
Permanent rejections stop immediately. An exhausted budget leaves the registration
unconfirmed and the runner stops before reading an OFFER. EC2 retains its spent
marker through failure and restart. If bursts exceed the qualified budget,
reduce the simultaneous startup demand and recover failed allocations through
the existing retirement procedure.

1. For a deployment using direct HELLO writes, use the previous release to [drain and retire every granted profile](#add-drain-and-retire-a-profile). Settle staged or executing JIT operations, issued offers, and running jobs while their original grants remain. Old runners can finish consuming already issued offers during this phase. Keep the old generation through confirmed session retirement and durable catalog tombstones for all prior profiles. This is required because switching to dry-run removes their grants.
2. Prepare a higher dry-run generation with fresh profile IDs for the new broker-backed bindings. Run `preflight retire` against the exact deployed runtime and confirm it authorizes the grant removal. Fence the old controller and use the protected deployment path for the dry-run generation. Revoke any remaining direct HELLO writers, including out-of-band role grants, and verify denial before continuing. Preserve the table and the lease, catalog, executor, effect and handoff records. The controller ignores any retained HELLO from the old partition namespace, even if it contains the broker's issuer field. Remove obsolete legacy rows with the operator recovery identity after accounting for all pre-cutover handoffs.
3. Use the matching stack outputs in `helloEndpoint` for the rebuilt AMIs and Fargate runner images. Review the exact Launch Template and task-definition revisions with the new controller. Check the IAM policies, TLS network path, and expected invocation throughput for each role. Exclude untrusted principals from direct invocation of the broker function and its qualifiers. Apply the active generation with the updated bindings and verify that older runner binaries cannot start a direct registration.
4. Use a compatible forward generation for rollback of unrelated settings. Preserve the broker URLs, runner revisions, and grants through the rollback. A controller from before this cutover cannot stage a broker-issued TTL record. Reverting the entire protocol requires a drained deployment and a separately reviewed migration.

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

### Recover a held Fargate or EC2 allocation

During drain or after a startup timeout, an exact Fargate task with an unconsumed
offer can be stopped automatically. The controller requires complete recent
inventory and a clear acquisition balance. It revokes the handoff and deletes
the exact encrypted offer in the same transaction as admission closure. A
bootstrap that already deleted its offer wins the race and the controller
holds that task. An uncertain stop remains in the journal for authoritative
readback. Keep the draining binding, task definition and grants until the
exact task is terminal and the handoff and runner registration are settled.

An online, idle GitHub runner may be assigned a job after it was last read.
The current SDK supplies no atomic scheduling fence for stopping a connected
Fargate runner. For a consumed offer that stays held, have the GitHub owner
first isolate scheduling for the exact scope, runner group and scale set
using a target-qualified procedure. Record the effective isolation and when
it took effect, then verify that existing assigned and running work has
finished. Check the exact runner ID, session, local acquisition balance and
unresolved journal entries. Preserve that isolation until the allocation is
terminal. If any assignment or request remains uncertain, keep the task held.
After independently confirming both the scheduling fence and the clear work
state, the AWS owner may stop the exact task ARN in its original cluster and
record the native readback. The controller reconciles that terminal observation
and the exact runner registration before retirement. This route requires
independent proof from the target. A drained local queue or a runner's idle
flag alone leaves the task held.

The controller reports an exactly bound stopped EC2 allocation with
`external_reclamation_required`. Keep its profile, instance ID, allocation,
Launch Template revision, and GitHub runner identity tied to the current
readback. Have the AWS owner verify the instance is still stopped and review
the durable job, handoff, acquisition and effect evidence with the GitHub
owner. Once outstanding work has an authoritative outcome and the original
runner cannot accept more jobs, reclaim the exact stopped instance under the
target's approved EC2 procedure and record the terminal instance observation.
The controller already settles exact termination readback and runner removal.
Pending, shutting-down, missing or conflicting observations require further
evidence before reclaiming the resource.

## Roll back or recover a controller

Roll back by deploying compatible known-good inputs as a **new, higher generation**. Compare current catalog and profile grants against the candidate before the deploy. Keep intervening profiles and bindings until their own retirement gates clear. Preserve tombstones and choose a new profile identity if the old profile is retired. Verify the writer protocol, data schema, CPU architecture, image digest, and runtime bytes before starting the replacement.

On an abrupt exit, allow the successor to acquire recovery-only authority in the
same ECS cluster. The successor requires an exact `STOPPED` observation for each
unclean predecessor and a current clock bound before reading back ambiguous
effects. An orderly release preserves the lease record and carries the clean
handover into the successor. Keep ambiguous acknowledgements and runner
registrations held. A new session opens with a fresh cursor after confirmed
retirement of the exact prior session. [Recovery and target boundaries](emulator-boundaries.md)

Protected deployment keeps the resolved controller cluster and its CDK
ownership fixed, preserving the existing `ecs:DescribeTasks` scope. Cluster
aliases can change if they resolve to the same cluster and ownership. To change
an EC2 supervisor binding, drain and tombstone the existing profile with its
original grants, pinned Launch Template version, AMI, role and qualification
evidence intact. Qualify the replacement under a fresh profile ID before taking
new work. A cluster migration, including a move through dry-run, requires a
separately reviewed transition that preserves the predecessor lookup.

### Isolate a predecessor whose ECS task is unavailable

1. Hold active deployments and admissions. Keep the tracking table and the
   last verified runtime generation. Use a consistent read from the deployed
   table with `PK=ENV#<account>:<region>:<environment>` and `SK=LEASE`.
   Record the exact deployment key, lease owner/task ARN, epoch, revision,
   transition ID, previous owner and required fencing task. Record unresolved
   effect and acknowledgement IDs, latest authorized attempt deadlines and the
   exact prior GitHub sessions.
   Save identifiers and evidence references without credential values.
2. Use the original account and Region to describe each exact task in the cluster
   named in its ARN. Save the request identity, matching response, timestamps and
   the old cluster and role bindings. Have the AWS owner restore the operator's
   scoped read access if the permission check failed. If ECS still reports the
   exact task as `STOPPED` and the deployed controller has that read scope, use
   the ordinary recovery path. Capture this promptly because stopped task
   history is limited.
   [ECS DescribeTasks](https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DescribeTasks.html)
3. If the exact task remains unavailable, record any pre-existing ECS task-state
   capture with the exact task ARN, cluster ARN, AWS account, event ID, timestamp
   and terminal status. Verify the capture provenance and access with an
   independent AWS reviewer. If terminal evidence is missing, have the AWS owner
   isolate the predecessor's mutation authority, including credential
   reacquisition and existing sessions. Have the GitHub owner independently
   verify isolation of the predecessor's mutation credentials and exact prior
   session. Retain audited evidence of effective isolation on both surfaces,
   including what must stay disabled, credential and session scope, and any
   shared identities affected. A changed secret value alone leaves previously
   issued credentials unresolved. [ECS event capture](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-lifecycle-events.html) · [IAM role session revocation](https://docs.aws.amazon.com/IAM/latest/UserGuide/id_roles_use_revoke-sessions.html) · [GitHub App installation endpoints](https://docs.github.com/en/rest/apps/installations)
4. Keep the decision and evidence IDs in the recovery record for each exact task
   and lease, with an evidence validity window and independent approval. A
   deployment with [qualified predecessor capture](controller-predecessor-capture.md)
   can use an exact terminal event after a matching `MISSING` result. If both
   sources lack accepted evidence, submit the record for a separately qualified
   cutover and keep the lease in recovery-only mode across restart. Keep possibly
   sent operations and sessions held until authoritative readback and the
   exact-session retirement path settle their outcomes. Preserve the lease and
   journal throughout the incident.

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
