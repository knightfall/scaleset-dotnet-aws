# Fargate runner bootstrap

Use one essential Linux runner container per task. Build the image with GitHub Actions Runner v2.338.2 at commit `d1eb12d4b115c301201de27d667284e1a80f2ab5` and pin the final image by digest. Verify the platform-specific release artifacts when building the image. Install the runner distribution, its .NET host, the bootstrap executable, the [child launcher](../src/Knightfall.ScaleSet.Aws.RunnerBootstrap/Fargate/runner-child.sh), and the nonsecret binding file under root-owned directories on the read-only image filesystem. The runner UID must be able to read and execute these files. [Runner release](https://github.com/actions/runner/releases/tag/v2.338.2)

## Task definition

Use a revision-pinned `FARGATE` task definition with `awsvpc`, the intended task and execution roles, and one essential container. Choose a non-root numeric user, enable `readonlyRootFilesystem`, leave restart disabled, and omit privileged mode, added capabilities, host devices, ECS Exec, and service attachment. Set `stopTimeout` to at least 30 seconds. The bootstrap uses up to 20 seconds to forward SIGTERM and drain the runner process before forced termination.

Mount one task-local Fargate ephemeral volume at the work path. Supply that path in `FargateContainerApproval.ScratchMountPaths`. The volume is for job work and tool data. Keep the approved command and every image asset outside it. Fargate manages the volume's capacity and lifetime, with the task definition controlling any increase from the platform default. [Fargate task storage](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/fargate-task-storage.html)

Make the mount writable by the configured non-root UID. For the example UID and path, add `RUN install -d -m 0700 -o 1001 -g 1001 /work` and `VOLUME ["/work"]` when building the image. The ECS mount path must match the declared volume path for ECS to carry the image's directory permissions into the data volume. [ECS bind mount permissions](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/bind-mounts.html)

For example, set the following fields on the existing task definition. Populate the image and roles with the approved values for the profile.

```json
{
  "requiresCompatibilities": ["FARGATE"],
  "networkMode": "awsvpc",
  "volumes": [{ "name": "work" }],
  "containerDefinitions": [
    {
      "name": "runner",
      "essential": true,
      "user": "1001:1001",
      "readonlyRootFilesystem": true,
      "stopTimeout": 30,
      "entryPoint": ["/opt/scaleset/RunnerBootstrap"],
      "command": ["fargate", "/opt/scaleset/fargate-bootstrap.json"],
      "mountPoints": [
        { "sourceVolume": "work", "containerPath": "/work", "readOnly": false }
      ]
    }
  ]
}
```

The exact image, entrypoint, command, and scratch path must match the profile's Fargate preflight approval. T16 launches one task with no runtime overrides. ECS supplies the metadata and task credential endpoint variables. Do not set environment entries, environment files, or secrets on the runner container.

The binding file contains only nonsecret deployment settings. This example uses an existing scale set. For an automatically created scale set, omit `scaleSetId`. The bootstrap then accepts the positive scale-set ID authenticated by the controller's scoped offer.

```json
{
  "account": "111122223333",
  "region": "ap-southeast-2",
  "deployment": "111122223333:ap-southeast-2:engineering-dev",
  "profileId": "linux-shell",
  "gitHubScope": "https://github.com/example/automation",
  "tableName": "scaleset-engineering-dev",
  "clusterArn": "arn:aws:ecs:ap-southeast-2:111122223333:cluster/engineering-shell",
  "taskFamily": "linux-shell-runner",
  "containerName": "runner",
  "taskRoleArn": "arn:aws:iam::111122223333:role/fargate-shell-bootstrap",
  "taskRoleId": "AROAEXAMPLETASK000001",
  "runnerDistribution": "/opt/scaleset/runner",
  "runnerDotnet": "/opt/scaleset/runner-dotnet/dotnet",
  "runnerChild": "/opt/scaleset/runner-child",
  "workScratch": "/work",
  "scaleSetId": 99
}
```

The bootstrap reads the container and task metadata locally, compares both container identities, and checks the exact task ID and role against the caller identity obtained with ECS task credentials. It gets the assigned revision from the task metadata and verifies it through the incarnation in the controller's offer. The task definition can therefore use the same command after registering a new revision. Replacing an IAM task role requires updating the expected role ID and registering the corresponding image/task revision. [Metadata v4](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-metadata-endpoint-v4-fargate-response.html), [ECS task role credentials](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-iam-roles.html)

Scope bootstrap storage to conditional `PutItem` for `HELLO#${aws:userid}` and conditional `DeleteItem` with `ALL_OLD` for `OFFER#${aws:userid}` on the configured table. Give this task role zero `STATUS#` writes, lifecycle-row access, Secrets Manager access, or GitHub administrative credentials. Workflow code may be able to use the same task identity. The controller gets resource death from verified ECS observations and job state from GitHub.

## RAM-backed runner configuration

The stock runner resolves configuration and diagnostics relative to its managed entry assembly. It creates and replaces credential files under that root, including during configuration refresh. The bootstrap verifies the entire mutable root on a bounded `/dev/shm` tmpfs with either `noswap` or a zero cgroup v2 swap limit before contacting DynamoDB. If that proof fails, bootstrap exits before publishing a hello or consuming an offer. [Pinned runner root resolution](https://github.com/actions/runner/blob/d1eb12d4b115c301201de27d667284e1a80f2ab5/src/Runner.Common/HostContext.cs#L372-L395), [Pinned credential writes](https://github.com/actions/runner/blob/d1eb12d4b115c301201de27d667284e1a80f2ab5/src/Runner.Common/ConfigurationStore.cs#L318-L358)

Install the runner's `bin` and `externals` directories under `runnerDistribution`. Use a .NET muxer at `runnerDotnet` with the pinned runner release's `libhostfxr.so` in its adjacent `host/fxr` layout. Install the root-owned executable child launcher at `runnerChild`. The bootstrap copies the three managed entries into RAM and links their remaining dependencies to the image. The muxer and child launcher can execute from the read-only image even when `/dev/shm` is mounted `noexec`. The staged managed entry assemblies keep the listener, worker, and plugin paths rooted in the private RAM layout. [ReadyToRun setting](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation#readytorun)

The decrypted encoded JIT payload stays in a disposable buffer. The bootstrap writes only the runner settings, credential data, and private runner key to its private RAM root, then erases the buffer before starting `Runner.Listener run --once`. It disables core dumps, makes the runner inherit Linux's no-new-privileges flag, keeps task credentials out of the child environment, discards raw runner standard output and error, and removes the RAM configuration root on exit. The `_work` link connects the runner to the separate task-local scratch mount.

## Qualification

Run the local Fargate Linux fixture with the pinned Linux ARM64 runner and .NET images already in the Docker cache.

```sh
tests/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks/run-fargate.sh
```

The offline fixture starts the actual pinned listener, worker, and plugin host under the no-execute RAM layout. Synthetic one-job and SIGTERM fixtures exercise the launcher, file placement, and cleanup. The SDK fixtures exercise metadata, STS identity, conditional single offer consumption, and the absence of Fargate status writes. T21 verifies synthesized IAM. T24 exercises the Fargate bootstrap application against local ECS and GitHub emulation using fixture AWS clients. The deployed command uses the configured Region and ECS credential endpoint.

`make check-integration` exercises the Dockerless Fargate scenario. MiniStack stores the task definitions, clusters, task rows, IAM roles, STS sessions, handoff records, EventBridge rules, and SQS messages. The suite uses the released GitHub client and listener against the loopback TLS simulator. A test transport narrows the ECS emulation gaps in MiniStack 1.5.44. It maps the emulator's UUID spelling to the ECS short task ID, supplies the attached ENI fields for fixture tasks, and holds the native `RunTask` receipt under its exact client token and request bytes for replay. A loopback metadata endpoint serves the native task identity to each bootstrap. A subprocess executes each simulated job. After that process exits, the fixture advances its task to native `STOPPED` and routes an ECS-shaped event through EventBridge to SQS. The consumer reads the exact task back before committing its terminal observation and acknowledging the queue message.

The local identity check uses actual MiniStack STS `RoleId:ShortTaskId` responses. The separate authorization fixture uses exact per-session leading keys because MiniStack cannot expand the `${aws:userid}` policy variable. Live platform qualification still requires real AWS policy evaluation, Fargate networking and vCPU quota evidence, and a runner image that satisfies the kernel requirements below.

Fargate does not support task-defined `tmpfs`, `sharedMemorySize`, or swap configuration. The deployment platform still needs to establish that its own `/dev/shm`, cgroup swap limit, task-local scratch mount, and architecture satisfy this contract. Record that result in T26 before qualifying a live Fargate runner platform. [Fargate task definition limits](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/fargate-tasks-services.html)
