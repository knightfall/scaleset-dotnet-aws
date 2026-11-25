# Direct EC2 runner bootstrap

The EC2 supervisor verifies its attached instance role, reserves one bootstrap attempt per instance, and starts a single ephemeral GitHub Actions runner. It uses STS and only the instance's item-scoped DynamoDB permissions. Windows is outside the supported bootstrap contract.

## Install and configure the AMI

Install the Linux build of `Knightfall.ScaleSet.Aws.RunnerBootstrap` and provision a root-owned system service. Run `RunnerBootstrap ec2 /etc/scaleset/bootstrap.json` as the supervisor. Use a restrictive umask, disable core dumps and hibernation, close inherited service descriptors, and do not restart the supervisor or runner to recover a job. Run only one supervisor service for the instance.

The settings file is root-owned, mode `0600`, with root-owned parent directories. It contains the table name, account, Region, bootstrap role ARN and its immutable IAM role ID, and the nonsecret controller deployment/profile/scope. Use the exact scale-set ID when the profile is pinned. For a controller-created set, leave it null and the scoped DynamoDB offer supplies the assigned ID.

```json
{
  "tableName": "scale-set-lifecycle",
  "helloEndpoint": "https://exampleid.lambda-url.ap-southeast-2.on.aws/",
  "authority": {
    "account": "111122223333",
    "region": "ap-southeast-2",
    "roleArn": "arn:aws:iam::111122223333:role/scaleset-ec2-bootstrap",
    "roleId": "AROAFIXTUREBOOTSTRAP01"
  },
  "target": {
    "deployment": "111122223333:ap-southeast-2:production",
    "profileId": "ec2-general",
    "scopeIdentity": "github.com/example/project",
    "scaleSetId": null
  },
  "spentDirectory": "/var/lib/scaleset-bootstrap",
  "runner": {
    "runtimeRoot": "/run/scaleset-runner",
    "boundaryProof": "/run/scaleset-bootstrap/verified-boundary.json",
    "listenerSha256": "<actual lowercase SHA-256 of the installed Runner.Listener>",
    "userId": 1500,
    "groupId": 1500
  },
  "publishStatus": false
}
```

Provision the spent directory on the instance's encrypted persistent `xfs` or `ext4` volume at mode `0700`. The supervisor creates and flushes the per-instance marker and its parent before HELLO. Retain that marker through reboot, update and failure. An unavailable offer can be polled with the same in-process key and nonce for up to five minutes. A lost DELETE response, invalid envelope or supervisor restart ends the attempt without re-registering.

Set `helloEndpoint` to the stack output for the configured instance role. The supervisor uses the verified role credentials to sign its registration and the same principal for one-time OFFER consumption. Qualify access to the exact HTTPS endpoint from the supervisor and keep the existing IMDS and process isolation checks for workflow code. See the [HELLO cutover](operator-runbook.md#cut-over-runner-hello-registration) for retained tables and older AMIs.

Install the architecture-matched GitHub Actions runner from the checksum-verified release package. The locally inspected Linux artifact was version `2.338.2` at commit `d1eb12d4b115c301201de27d667284e1a80f2ab5`. Mount the whole mutable runner root as a private executable `tmpfs` with `nosuid,nodev`, root ownership and the dedicated runner group, and set mode `1770`. Ensure that swap is off, or use an explicitly `noswap` mount and a cgroup v2 swap limit of zero for the supervisor and descendants. Follow the filesystem layout in the archive and protect packaged programs and libraries from modification by the runner UID. Create the mount anew at boot and unmount it at shutdown. [GitHub Actions runner release](https://github.com/actions/runner/releases/tag/v2.338.2), [Linux tmpfs documentation](https://docs.kernel.org/filesystems/tmpfs.html), [cgroup v2 swap limits](https://docs.kernel.org/admin-guide/cgroup-v2.html#memory-interface-files).

The bootstrap materializes only supported decoded members of the authenticated JIT payload at the volatile root and erases its plaintext buffers before starting the listener. It invokes the pinned `Runner.Listener run --once` directly under the configured non-root UID/GID, clears inherited environment and supplementary groups, drops capabilities and sets no-new-privileges. Keep the whole runner diagnostic directory volatile as well. The one-job runner can read its own short-lived GitHub runner credentials. It receives no supervisor role credentials, raw JIT config, PAT or App secrets. [Upstream JIT and one-job runner code](https://github.com/actions/runner/blob/d1eb12d4b115c301201de27d667284e1a80f2ab5/src/Runner.Listener/Runner.cs#L234-L331).

## Qualify the isolation boundary before enabling status

At boot, a trusted privileged provisioning service must validate the deployed AMI's isolation and attest to that boot. The supervisor requires a root-owned, mode `0600` proof under root-owned directories. The proof contains these fields.

| Field | Required value |
| --- | --- |
| `protocolVersion` | `1` |
| `bootId` | Exact current `/proc/sys/kernel/random/boot_id` |
| `runtimeRoot`, `listenerSha256`, `userId`, `groupId` | Exact validated launcher settings |
| `processIsolationVerified` | Positive process-isolation checks from the workflow UID and Docker job |
| `ipv4MetadataDenied`, `ipv6MetadataDenied` | Installed address deny rules and matching connectivity probes |
| `dockerBoundaryVerified` | Positive verification of the local rootless Docker boundary |
| `jobProcessTeardownVerified` | For opt-in STATUS, positive verification that job processes are contained through normal and forced teardown |

The qualification service must fail the boot gate when any of these checks fails:

- Hide privileged processes from the workflow user and its containers. Check that job code cannot read protected process arguments or environment, attach with ptrace, or access the supervisor's credential and state directories. Keep the proof and service tools outside paths writable by the runner user.
- Enforce IMDSv2 at the Launch Template. Apply the installed host and container network policy to both `169.254.169.254` and `fd00:ec2::254`. Cover the unprivileged runner and all local rootless Docker network helpers, including any permitted host-network mode. Check the installed rules as well as negative probes from real job processes. A timeout or currently disabled endpoint alone does not establish a deny rule.
- Use a rootless local Docker daemon under the dedicated runner account. The bootstrap points Docker at that account's socket. Qualify the socket ownership and daemon mode, plus each supported job network and container mode. The runner account must have no sudo, rootful Docker socket, privileged supervisor namespace access, or credential relay. [Docker rootless mode](https://docs.docker.com/engine/security/rootless/).
- Verify the supervisor alone can retrieve its attached role, and within the trusted test setup verify an attempted status write from workflow code is denied. Recheck after AMI, networking, runner package, or container runtime changes. Clear old proofs when the verified boundary changes.

The default leaves `publishStatus` disabled and requires external reclamation of still-running instances. To opt in, use the same `statusQualification` object in the root-owned bootstrap settings and the [controller EC2 profile](profile-bindings.md#qualify-ec2-supervisor-cleanup). Set `publishStatus` to `true` and include `statusQualificationSha256` from that object's `evidenceSha256` and `jobProcessTeardownVerified: true` in the current-boot proof. The supervisor checks the proof, the role identity, and the AMI from IMDSv2 before publishing. Readiness follows the pinned listener's session-established signal. Qualified exit requires the job namespace to exit, the redirected streams to close within the teardown budget, and credential cleanup to succeed. [Runner session readiness signal](https://github.com/actions/runner/blob/d1eb12d4b115c301201de27d667284e1a80f2ab5/src/Runner.Listener/Runner.cs#L472-L491)

The qualified launch requires trusted `/usr/bin/unshare` and `/usr/bin/bash` and a Linux service that can create a private PID namespace and mount its procfs. The privileged namespace init stays separate from the listener's runner UID. Verify that a detached child dies on listener exit and on supervisor cancellation without affecting processes outside the namespace. Include work delegated through local service sockets in this qualification. If an allowed Docker daemon or other helper can start job processes outside this boundary, leave STATUS disabled and reclaim the instance externally. Shell-only profiles can qualify with the external job-launch sockets inaccessible to workflow code. [Linux PID namespaces](https://man7.org/linux/man-pages/man7/pid_namespaces.7.html)

An alpha.1 proof without `jobProcessTeardownVerified` still works for unqualified launch. Before upgrading a STATUS-enabled instance, requalify the AMI and generate the new proof at boot. Regenerate the previous proof format if rolling back. If cleanup is incomplete, the supervisor reports the failure and withholds trusted exit. Cancellation also keeps the allocation held pending external reconciliation. Keep the spent marker, fence the exact instance from new work, and use the [external reclamation guidance](profile-bindings.md#qualify-ec2-supervisor-cleanup).

## Local verification

`make check-lifecycle` covers bootstrap wire identity, offer consumption, failure and restart behavior using synthetic credentials and loopback MiniStack. The separate `tests/Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks/run.sh` smoke check exercises the Linux privilege drop, volatile credentials, one readiness signal, and the fsynced marker. It proves namespace teardown for normal and canceled runs with detached output writers, preservation of an unrelated process, and continued cleanup through symlinks and directory collisions. It requires Docker with the pinned local ARM64 SDK image or a compatible local image supplied through `SCALESET_LINUX_FIXTURE_IMAGE`. It uses synthetic material and runs in a disposable network-disabled Linux container. The fixture does not establish AWS IAM or AMI network isolation. Use T23 for the emulated whole flow and T26 for deployment-bound host and job verification.
