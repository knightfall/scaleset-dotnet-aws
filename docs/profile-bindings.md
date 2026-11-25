# Bind runner profiles to AWS and GitHub

Start with [the mixed profile template](examples/mixed-profile-bindings.yaml). It has one Direct EC2 profile and one Fargate profile, with separate runner groups and labels. It starts in dry-run mode with AWS inspection disabled. Replace the example identifiers before use. Follow the [operator runbook](operator-runbook.md) for deployment, drain, rollback, and destruction. Record target results alongside the [emulator evidence](emulator-boundaries.md).

## Choose and record the bindings

| Configuration | Record and verify |
| --- | --- |
| `metadata.name`, `spec.aws`, profile `id` | Keep the deployment identity `<account>:<region>:<metadata.name>` and each profile ID stable through updates. Use the same identity in the runner bootstrap settings. |
| `spec.tracking.table` | Use `import` with an existing table and pass table preflight. Use `create` for a new CDK-managed retained table named `<metadata.name>-runner-lifecycle`. Keep the exact table name in each bootstrap configuration. |
| `spec.networks` | Supply the existing VPC, exact subnet IDs and their Availability Zones, and security group IDs. Verify that the selected subnets, routes, and security groups support the provider's intended private networking and egress. |
| `spec.ecsClusters` | `create` manages the named cluster. `import` references the exact existing cluster ARN. The controller and Fargate runner profiles select a named cluster entry. |
| `spec.github` and profile `github` | Verify the canonical GitHub scope, existing runner group, unique scale-set name, and managed labels. Include the scale-set name in the labels and keep labels disjoint per scope. `existing` can pin a positive quoted decimal `scaleSetId`. `create-if-missing` can create the scale set in the existing group and omits the ID. |
| EC2 `provisioner` | Bind a Launch Template ID and a quoted numeric version. Record the resolved AMI, platform, instance type, subnet and security group set, instance profile, and bootstrap role. |
| Fargate `provisioner` | Bind the exact cluster, revision-qualified task definition ARN, runner container name, bootstrap task role, and separate execution role. |
| Profile `capacity` | Set the maximum, idle reserve, and startup timeout for each workload. Use explicit, conservative initial limits while qualifying the target. |

The stack creates the controller service and its task and execution roles. It references existing networking and runner definitions. In active mode it attaches narrow bootstrap policies to the referenced runner roles. Include those role policy changes in the CDK diff review. Retain the prior bindings when a profile drains so the controller can still observe and reconcile its resources.

For the qualification workflow, use `runs-on: build-ec2` or `runs-on: build-fargate` after substituting the selected managed label. Verify the runner group allows the target repository and the job dispatches only to the intended profile.

Use the approved controller image's exact manifest digest in `spec.controller.image`. After inserting or changing it, re-emit the runtime artifact. Record the image manifest digest, emitted runtime SHA-256, S3 object version ID, and positive deployment generation as separate fields. The runtime SHA-256 covers the exact emitted bytes. CDK and the controller verify the runtime binding using the selected digest and object version.

The EC2 bootstrap settings use the GitHub scope in canonical identity form, such as `github.com/example-engineering/builds`. The Fargate settings accept the configured GitHub URL. Both take the exact deployment identity, profile ID, table name, and bootstrap role ARN and immutable IAM `RoleId`. Use the configured scale-set ID when one is pinned. When a role is recreated, verify its new `RoleId` and update the AMI or task image binding before launch. [EC2 settings](ec2-runner-bootstrap.md#install-and-configure-the-ami) · [Fargate settings](fargate-runner-bootstrap.md#task-definition) · [IAM role lookup](https://docs.aws.amazon.com/cli/latest/reference/iam/get-role.html)

## Verify the template offline

Use the pinned Node, npm, and .NET SDK from the repository. From the checkout root, install the locked CDK dependencies and build the Release controller. Compile and verify the sample without reaching AWS.

```sh
output_dir=$(mktemp -d)
project=src/Knightfall.ScaleSet.Aws.Controller
controller="$project/bin/Release/net10.0/Knightfall.ScaleSet.Aws.Controller.dll"
runtime="$output_dir/runtime.json"

npm --prefix cdk ci --engine-strict --ignore-scripts
dotnet restore "$project" --locked-mode
dotnet build "$project" --configuration Release --no-restore
runtime_sha256=$(npm --prefix cdk run --silent config:emit -- \
  "$PWD/docs/examples/mixed-profile-bindings.yaml" "$runtime")
dotnet "$controller" config verify --file "$runtime" --sha256 "$runtime_sha256"
OTEL_SDK_DISABLED=true dotnet "$controller" reconcile --config "$runtime" \
  --sha256 "$runtime_sha256" --dry-run --once

(
  cd cdk
  ./node_modules/.bin/cdk synth --strict --no-lookups --output "$output_dir/cdk.out" \
    --app 'node bin/controller.ts --config ../docs/examples/mixed-profile-bindings.yaml --generation 1'
)
```

The report should include `build-ec2` and `build-fargate`, no performed AWS inspection, and no proposed starts. Confirm that the synthesized dry-run stack omits AWS compute mutation, GitHub credential, and runner queue grants.

## Qualify imported encryption keys

Keep the existing stack-owned S3 destination and tracking table defaults. Use the optional `--encryption` input on both `node bin/controller.ts` and `deploy:controller` when supplying customer-managed keys for imported resources. The JSON accepts only the applicable entries below. Each entry names a literal key ARN in the deployment account and Region. Keep the file with the reviewed deployment inputs. Key ARNs are nonsecret.

```json
{
  "bootstrapAssetKeyArn": "arn:aws:kms:ap-southeast-2:111122223333:key/00000000-0000-0000-0000-000000000001",
  "trackingTableKeyArn": "arn:aws:kms:ap-southeast-2:111122223333:key/00000000-0000-0000-0000-000000000002"
}
```

| Binding | Calling principal and required access |
| --- | --- |
| CDK bootstrap asset, `bootstrapAssetKeyArn` | The CDK file-publishing role needs its existing S3 and KMS publishing access, including S3-mediated `kms:GenerateDataKey` for SSE-KMS uploads. The runtime-copy Lambda independently needs S3-mediated `kms:Decrypt` on the exact runtime source object. Its IAM policy binds the key ARN, regional S3 service, and `aws:s3:arn` object context. The Lambda destination remains SSE-S3. CDK and CloudFormation may also use the bootstrap key for their own artifacts under the existing bootstrap policies. |
| Imported lifecycle table, `trackingTableKeyArn` | DynamoDB decrypts the table key on behalf of the active controller task role, HELLO broker Lambdas, and bootstrap roles that access the imported table. The stack provides `kms:Decrypt` on the exact key through regional DynamoDB with `aws:dynamodb:tableName` and `aws:dynamodb:subscriberId` bound to this table and account. The deployment/preflight identity needs its own matching data access through DynamoDB. The key owner retains the existing DynamoDB maintenance grants, including the table-specific grant created when the key was selected. |
| Versioned dry-run snapshot, `snapshotKeyArn` | The dry-run controller task role uses S3-mediated `kms:Decrypt` on the exact snapshot object context. The separate S3 statement and SDK request pin the object version and owner. Local `reconcile` uses the invoking AWS principal's permissions. |
| Fargate quota object, `fargateQuotaKeyArn` | The controller task role uses S3-mediated `kms:Decrypt` on the exact current object. Keep subsequent samples under the same key. The separate publisher needs its own scoped S3 write and SSE-KMS publishing permissions. |

Before the protected deployment, verify the actual caller with `sts get-caller-identity`. Review `kms describe-key` and `kms get-key-policy --policy-name default` for the selected key. The key owner can delegate use to same-account IAM policies or authorize the exact caller role ARNs directly with the service and encryption context above. For DynamoDB, have the key owner check `kms list-grants` for the existing table-specific service grants and their table/account constraints. Keep those grants and the prior usable key in place through maintenance and migration. The stack attaches identity policies and leaves key policies and grants under the key owner's control. Review the file publishing, CloudFormation, Lambda, controller, runner, and operator principals separately. [KMS key policies](https://docs.aws.amazon.com/kms/latest/developerguide/key-policies.html) · [DynamoDB key permissions and service grants](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/encryption.usagenotes.html) · [CDK bootstrap roles](https://docs.aws.amazon.com/cdk/v2/guide/bootstrapping-env.html)

For a nominated bootstrap key, the wrapper uses `GetBucketEncryption` on the exact synthesized source bucket before preparing the change set and `HeadObject` on the published asset before execution. For nominated snapshot and quota keys it checks the exact objects with `HeadObject`, including the configured snapshot version. The operator needs metadata-read permission on each opted-in resource and `kms:DescribeKey` on its key. Use single-layer SSE-KMS with S3 Bucket Keys disabled for these object-context grants. If the wrapper reports an encryption mismatch, publish a matching object under the reviewed key before retrying. An AWS-owned DynamoDB table key needs no KMS lookup. When an imported table reports a KMS ARN, `DescribeKey` distinguishes AWS-managed encryption from customer-managed encryption, which requires a matching binding. The built-in table and ordinary bootstrap path add no key lookups. [S3 object encryption context](https://docs.aws.amazon.com/AmazonS3/latest/userguide/specifying-kms-encryption.html) · [S3 HeadObject](https://docs.aws.amazon.com/AmazonS3/latest/API/API_HeadObject.html) · [DynamoDB table encryption metadata](https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_SSEDescription.html)

Metadata and offline policy checks establish the binding and proposed permissions. For target qualification, confirm a copy, snapshot read, quota read, and table client access through the intended role paths under separately authorized procedures. In particular, an operator's successful `HeadObject` or `DescribeKey` does not establish the runtime role's ability to decrypt. A quota read failure holds new starts. For an established imported table binding, retain the previously recorded key ARN during a normal rollout and coordinate an external key migration as a separate reviewed cutover.

### EC2 volume keys

The controller task role calls `RunInstances`; EC2 and EBS use instance identity-only roles and service-created grants for volume attachments. The runner bootstrap instance profile is a separate caller and needs no EBS key permissions. The generated controller policy does not authorize EBS customer-managed keys. The controller holds starts with `ebs_key_unqualified` when the selected Launch Template explicitly specifies a key other than the regional `alias/aws/ebs`. Use a newly qualified template and AMI with an AWS-managed EBS key to clear the hold. Inventory and existing-instance recovery remain available.

In the read-only EC2 preflight, record `DescribeLaunchTemplateVersions` block mappings, `DescribeImages` snapshot IDs, `DescribeSnapshots` encryption and keys, and `GetEbsDefaultKmsKeyId` in the target account and Region. An omitted template key can still select a customer-managed account default or encrypted source snapshot. Hold activation when those effective keys are unknown or customer managed. A separately managed CMK launch path needs reviewed exact source and destination keys, the `RunInstances` role's required EC2-mediated key actions and service-only `CreateGrant`, plus independent qualification of the identity-only role and launch behavior. EC2 dry-run authorization and key metadata cannot prove asynchronous attachment or effective grants. [EBS encryption requirements](https://docs.aws.amazon.com/ebs/latest/userguide/ebs-encryption-requirements.html) · [EBS-backed AMI encryption](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/AMIEncryption.html)

## Collect target evidence before enabling launches

Run these read-only checks from a trusted operator environment with AWS CLI v2. Set `aws_profile` and `aws_region` for the intended account and Region. Use the resource names and ARNs from the candidate binding, and check the caller before trusting any service response.

```sh
aws_read() {
  aws --profile "${aws_profile:?set aws_profile}" --region "${aws_region:?set aws_region}" --no-cli-pager "$@"
}

aws_read sts get-caller-identity --query '{Account:Account,Arn:Arn}' --output json
aws_read ec2 describe-launch-template-versions --launch-template-id "$launch_template_id" \
  --versions "$launch_template_version" \
  --query 'LaunchTemplateVersions[].{Version:VersionNumber,Image:LaunchTemplateData.ImageId,Type:LaunchTemplateData.InstanceType,Metadata:LaunchTemplateData.MetadataOptions,Profile:LaunchTemplateData.IamInstanceProfile}' \
  --output json
aws_read iam get-instance-profile --instance-profile-name "$instance_profile_name" \
  --query 'InstanceProfile.{Arn:Arn,Roles:Roles[].Arn}' --output json
aws_read iam get-role --role-name "$bootstrap_role_name" --query 'Role.{Arn:Arn,RoleId:RoleId}' --output json
aws_read ecs describe-task-definition --task-definition "$task_definition_arn" \
  --query 'taskDefinition.{Arn:taskDefinitionArn,Status:status,Compatibility:requiresCompatibilities,Network:networkMode,Platform:runtimePlatform,Cpu:cpu,TaskRole:taskRoleArn,ExecutionRole:executionRoleArn,Containers:containerDefinitions[].{Name:name,Image:image,User:user,Essential:essential,Privileged:privileged,ReadOnly:readonlyRootFilesystem}}' \
  --output json
aws_read ec2 describe-subnets --subnet-ids "$runner_subnet_a" "$runner_subnet_b" \
  --query 'Subnets[].{Id:SubnetId,Vpc:VpcId,Zone:AvailabilityZone,IPv4:AvailableIpAddressCount,State:State}' \
  --output json
aws_read secretsmanager describe-secret --secret-id "$github_secret_arn" \
  --query '{Arn:ARN,Key:KmsKeyId,Stages:VersionIdsToStages}' --output json
```

Keep the verification record limited to resource identifiers, approved digests, policy decisions, timestamps, and test outcomes. The projections above omit EC2 user data, container environment values, and secret values. Inspect the AMI and container configuration in the trusted build process when verifying exact approved arguments and mounts. [Launch Template inspection](https://docs.aws.amazon.com/cli/latest/reference/ec2/describe-launch-template-versions.html) · [Task definition inspection](https://docs.aws.amazon.com/cli/latest/reference/ecs/describe-task-definition.html) · [Secrets Manager metadata](https://docs.aws.amazon.com/cli/latest/reference/secretsmanager/describe-secret.html)

### Direct EC2 AMI and IMDS

1. Verify the exact numeric Launch Template version and AMI on the target. Check that the instance has IMDSv2 required, the expected attached instance profile and role, and the architecture-matched pinned runner. Resolve and record aliases if the profile uses one. Requalify an AMI or version change before relying on the new boot boundary.
2. Run the privileged supervisor as its own root-owned service. Exercise the intended job under the dedicated unprivileged UID/GID on the host and inside each supported rootless Docker job networking mode, including host networking if allowed. Verify no rootful Docker socket or sudo access, no inherited supervisor descriptors or environment, and that the job cannot open protected process or state files or attach to the supervisor. Use a disposable process with equivalent protection for a ptrace denial test. Test file access without dumping process arguments or environment.
3. Install persistent host and container network deny rules for IPv4 `169.254.169.254` and IPv6 `fd00:ec2::254`. Verify the effective rules and which UID, network namespaces and rootless helpers they cover. A disabled endpoint, an IMDSv2 hop limit, or a timeout alone gives insufficient evidence of isolation. Run the probes below from the real workflow paths after the network and firewall are up.
4. From the trusted supervisor context, verify that the role-dependent bootstrap succeeds and the AWS caller's `UserId` matches the expected `RoleId:ShortInstanceId`. Keep that positive control separate from the job probes. Verify the root-owned RAM runner root, swap policy, credential isolation, spent marker, and one-job execution on the same target. [EC2 launcher and boot proof](ec2-runner-bootstrap.md#qualify-the-isolation-boundary-before-enabling-status)

The probe discards response bodies, including any IMDSv2 token. Any HTTP response, including `401`, proves that the job reached the endpoint. Require both the failed connectivity probes and the installed deny rules.

```sh
for address in '169.254.169.254' '[fd00:ec2::254]'; do
  base="http://$address"
  put_status=$(curl --disable --silent --noproxy '*' --connect-timeout 2 --max-time 4 \
    --output /dev/null --write-out '%{http_code}' --request PUT \
    --header 'X-aws-ec2-metadata-token-ttl-seconds: 60' "$base/latest/api/token" || :)
  get_status=$(curl --disable --silent --noproxy '*' --connect-timeout 2 --max-time 4 \
    --output /dev/null --write-out '%{http_code}' "$base/latest/meta-data/instance-id" || :)
  if [ "$put_status" != 000 ] || [ "$get_status" != 000 ]; then
    printf 'IMDS isolation failed for %s (PUT %s, GET %s)\n' "$address" "$put_status" "$get_status" >&2
    exit 1
  fi
done
```

### Qualify EC2 supervisor cleanup

The default uses external reclamation for still-running hosts. The dry-run report lists that mode as `external-reclamation-required`. GitHub completion alone leaves the host held. Exact EC2 terminal readback releases the allocation even when the primary network interface has detached.

To enable automatic cleanup, qualify the exact AMI, pinned numeric Launch Template version, dedicated EC2 bootstrap role and immutable IAM role ID, runner version, UID/GID, and supported Docker modes. Record the results and hash the reviewed qualification evidence. Set the optional EC2 provisioner field as shown below.

```yaml
statusQualification:
  imageId: ami-0123456789abcdef0
  roleId: AROAFIXTUREBOOTSTRAP01
  evidenceSha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
```

Copy the same object into the root-owned [bootstrap settings](ec2-runner-bootstrap.md#qualify-the-isolation-boundary-before-enabling-status), set `publishStatus: true`, and write the matching root-owned current-boot proof only after the checks pass. The active stack grants `PutItem` for `STATUS#${aws:userid}` on the lifecycle table to that EC2 role, with EC2-sourced IMDSv2 credentials required. The role must be dedicated to the qualified profile. The controller validates the configured AMI and role ID in preflight and reads status only for the matching running instance and exact handoff. Cleanup waits for authenticated process exit, GitHub completion, and the log flush grace period. Qualify the actual AWS IAM grant and denial from the workflow and nested containers before depending on it.

The STATUS statement permits only the table ARN and the status attribute set, which excludes lifecycle index attributes. The supervisor can recover a lost exit-write response by replaying the same observation. DynamoDB supplies the item checked by a failed condition, and the supervisor confirms it only when the entire status matches. Qualify the successful conditional readback and verify denial of writes carrying lifecycle index attributes in the target. If the supervisor cannot confirm exit within its retry budget, keep the instance held and follow external reclamation using independent job and instance evidence. [DynamoDB attribute conditions](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/specifying-conditions.html) · [Conditional write readback](https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_PutItem.html)

Drain and reclaim runners from a prior binding before replacing the AMI, Launch Template version, or qualification evidence. Remove `statusQualification` from the controller and set `publishStatus: false` on the image when the qualification expires or no longer applies. [AWS EC2 credential condition keys](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_condition-keys.html) · [AWS IMDS access controls](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/instance-metadata-limiting-access.html)

### Fargate task, network, and quota

1. Verify the exact active cluster and task definition revision. Require Fargate compatibility, Linux on the selected architecture, `awsvpc`, one approved essential non-root container, a read-only root filesystem, no privileged mode or ECS Exec, and exact task and execution role bindings. Compare the image manifest digest, entry point, command, and scratch mounts to the approved runner image and target definition. Keep the task definition and `RunTask` arguments free of runner secrets and overrides.
2. Verify the controller principal's effective `taskLongArnFormat` setting and an actual task's long ARN. The controller checks the setting with its own credentials. The equivalent read is `aws ecs list-account-settings --name taskLongArnFormat --effective-settings` under that same principal. Confirm the chosen subnet and the exact task's attached ENI, including subnet ID, ENI ID, and private IPv4 address. Check that Task Metadata v4 identifies the same task and container, and that the task role's caller identity is the expected `RoleId:ShortTaskId`. [ECS account settings](https://docs.aws.amazon.com/cli/latest/reference/ecs/list-account-settings.html) · [ECS task metadata](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-metadata-endpoint-v4-fargate-response.html)
3. Run the approved runner on the actual Fargate platform, with its real `/dev/shm` mount flags and task-local scratch volume. Verify the bounded RAM root and either `noswap` or a zero effective cgroup v2 swap limit. Exercise the image's one-job launcher with `noexec` RAM storage, using the executable muxer and child launcher in the read-only image. Verify RAM cleanup and native terminal task evidence after the essential process exits. Qualify the task role's authenticated HELLO endpoint and exact OFFER access, and verify that it has zero `STATUS#` access. [Fargate RAM-backed runner configuration](fargate-runner-bootstrap.md#ram-backed-runner-configuration)
4. Determine the applied On-Demand Fargate vCPU quota in the selected account and Region with `aws_read service-quotas list-service-quotas --service-code fargate`. Pair it with fresh, conservative account usage for `AWS/Usage` `ResourceCount` with dimensions `Service=Fargate`, `Type=Resource`, `Resource=vCPU`, and `Class=Standard/OnDemand`. Convert vCPU headroom to CPU units at 1,024 per vCPU. Record remaining headroom after observed usage and reserve headroom for other workloads. Verify subnet IPv4 availability alongside CPU. Known zero holds launches. Unavailable, ambiguous, or stale evidence also holds new launches. [Applied quotas](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-quotas.html) · [Fargate usage metrics](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/monitoring-fargate-usage.html)

Supply the exact per-profile image, entry point, command, and scratch paths through `SCALESET_FARGATE_PREFLIGHT`. To refresh launch headroom without redeploying, configure `quotaSource` with the bucket and exact object key of an observation published by a trusted operator or scheduled job. Use a private, general-purpose S3 bucket in the deployment account and Region, with a bucket name without dots. Use S3-managed encryption or the [opt-in customer-managed key binding](#qualify-imported-encryption-keys). CDK grants the controller `s3:GetObject` on that exact object. The controller also supplies the deployment account as the expected bucket owner. Restrict writes to that object to the observation publisher. [Controller preflight input](controller-local.md#controller-preflights) · [S3 expected bucket owner](https://docs.aws.amazon.com/AmazonS3/latest/userguide/bucket-owner-condition.html)

Publish a complete UTF-8 JSON object of at most 4 KiB in one `PutObject`. The following example shows the payload shape.

```json
{
  "account": "111122223333",
  "region": "ap-southeast-2",
  "observedAt": "2026-11-24T09:00:00Z",
  "expiresAt": "2026-11-24T09:10:00Z",
  "remainingCpuUnits": 8192,
  "reservedCpuUnits": 1024
}
```

Use the start of the measured usage window for `observedAt` so delayed metrics cannot make tracked launches disappear from the reservation calculation. Cover all regional On-Demand Fargate usage, including ECS services and EKS Fargate pods. Reserve CPU margin for workloads and pending allocations whose usage may lag the metric. Include launch pressure from controllers outside the deployment lease in that margin. Publish a new observation before the old sample expires and validate the source against known workload usage in the target. During qualification, stop the publisher or deny the controller's object read and check that new starts hold, then restore a current sample and check that they resume. [Fargate regional usage](https://docs.aws.amazon.com/eks/latest/userguide/monitoring-fargate-usage.html)

The controller checks the source at most once per 30 seconds in a reconciliation pass, with a three-second read budget. A sample must match the configured account and Region, be at most ten minutes old, and be before its expiry. Read errors and invalid or expired samples hold new starts. The controller combines the sample with tracked launch reservations across the deployment and current subnet IP evidence. First-send authorization for a planned launch expires with that quota observation. S3 qualification should include the actual task role and its network path to S3. An older inline `quota` observation remains supported when no source is set and expires on the same schedule.

### Secrets and role separation

Use separate IAM roles for each purpose. An active or draining profile's bootstrap role cannot also be the Fargate execution role in any active or draining profile. The configuration compiler and controller identify overlaps by IAM role name in the deployment account, including path and case variants. If an alpha.1 configuration used a shared identity for both purposes, register a task definition revision with an execution-only role and update the profile to match. Retain the original role and revision until their allocated runners have retired, and use the protected deployment preflight for changes to existing bindings. A supervisor-qualified EC2 bootstrap role still requires a dedicated identity. [IAM role naming](https://docs.aws.amazon.com/IAM/latest/APIReference/API_CreateRole.html)

Same-purpose sharing is supported for execution roles and for bootstrap roles without qualified supervisor STATUS access. A bootstrap role used by multiple profiles can invoke their broker URLs. The broker binds the caller to its exact AWS resource and allocation, and the table grant scopes offer reads to the caller's AWS session identity. Review all shared role policies and trust relationships before sharing a bootstrap role. Isolate workloads that require separate controller authority in distinct deployments.

The controller role can launch through the configured EC2 templates and pass the configured instance roles to EC2 instances in the deployment account. EC2 network grants require the profile's template ARN and allow only its configured subnet and security group resources. Instance authorization also requires the template's bound instance profile and ownership tags. Fargate launch grants pair each exact task definition revision with its configured cluster and ownership tags, and require ECS Exec disabled. The controller verifies networking and both roles against the definition and sends the approved revision with no role overrides. ECS IAM does not supply `RunTask` conditions for the two role slots or the awsvpc subnet and security group IDs. A compromised controller has the union of the synthesized grants, including all configured Fargate roles passed to ECS tasks, so the profile pairing depends on the integrity of the controller. [EC2 authorization](https://docs.aws.amazon.com/service-authorization/latest/reference/list_amazonec2.html) · [ECS authorization](https://docs.aws.amazon.com/service-authorization/latest/reference/list_amazonelasticcontainerservice.html) · [IAM condition keys for PassRole](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_iam-condition-keys.html#ck_AssociatedResourceArn)

Verify that the active controller task role can read only the configured full GitHub secret ARNs at `AWSCURRENT`. If a customer-managed KMS key protects a secret, verify its key policy and the controller grant for decryption through regional Secrets Manager with the exact secret encryption context. The deployer can use `DescribeSecret` and review the secret resource policy without obtaining values. Keep GitHub administration credentials out of both runner roles and execution roles. Keep any OTLP header secret injection on the controller execution role. Review identity, resource, and key policies and confirm the intended allow and deny outcomes in the target. [AWS Secrets Manager IAM policies](https://docs.aws.amazon.com/secretsmanager/latest/userguide/auth-and-access_iam-policies.html) · [Controller telemetry settings](opentelemetry-integration.md#configuration-contract)

Use only the secret ARN in the YAML. The GitHub App secret JSON has `clientId`, a positive integer `installationId`, and `privateKey`. The enterprise PAT form has `token` and belongs to an enterprise scope. Populate these values through the authorized secret-management workflow.

When a full Fargate warm reserve serves an offered job, the controller checks membership in the bound scale set and confirms that the exact runner is online and idle through the scoped GitHub REST runner endpoint. Qualification should verify this read for a JIT runner using the configured repository or organization App installation, or enterprise PAT. An unavailable or inconclusive read leaves the warm slot unavailable for admission. [GitHub REST runner status](https://docs.github.com/en/enterprise-cloud@latest/rest/actions/self-hosted-runners)

Keep the tested target and authentication scope with the release record. The template's GitHub App binding needs its own target qualification. The released SDK's existing live evidence covers GitHub.com repository scope with a personal-account credential. [Released SDK qualification](dependency-baseline.md#session-recovery-and-qualification-boundary)
