# scaleset-dotnet-aws

[![CI](https://github.com/knightfall/scaleset-dotnet-aws/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/knightfall/scaleset-dotnet-aws/actions/workflows/ci.yml)
[![Controller release candidate](https://github.com/knightfall/scaleset-dotnet-aws/actions/workflows/release-candidate.yml/badge.svg?branch=main)](https://github.com/knightfall/scaleset-dotnet-aws/actions/workflows/release-candidate.yml)
[![GitHub release](https://img.shields.io/github/v/release/knightfall/scaleset-dotnet-aws?include_prereleases)](https://github.com/knightfall/scaleset-dotnet-aws/releases)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/knightfall/scaleset-dotnet-aws/badge)](https://scorecard.dev/viewer/?uri=github.com/knightfall/scaleset-dotnet-aws)
[![Apache 2.0 license](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

A .NET 10 controller for GitHub Actions runner scale sets on AWS. The controller
runs on ECS Fargate and provisions ephemeral runners on Direct EC2 or standalone
Fargate tasks using the released `Knightfall.ScaleSet.Client` and
`Knightfall.ScaleSet.Listener` packages.

The implementation, CDK infrastructure, and local integration suites are in
this repository. The operator guides cover deployment, recovery, and the checks
required in each target environment.

- [Implementation plan](docs/implementation-plan.md): architecture, configuration,
  lifecycle rules, and verification boundaries.
- [OpenTelemetry integration plan](docs/opentelemetry-integration.md): signal
  contracts, per-profile visibility, OTLP configuration, and acceptance evidence.
- [Released SDK baseline](docs/dependency-baseline.md): exact package version,
  release evidence, supported Listener APIs, and session-recovery limits.
- [Task index](docs/tasks/README.md): 26 tasks across six milestones, with
  dependencies, deliverables, and acceptance criteria.
- [Agent instructions](AGENTS.md): repository layout and engineering conventions.

## Get the controller

[GitHub Releases](https://github.com/knightfall/scaleset-dotnet-aws/releases)
is the distribution point for versioned Linux amd64 and arm64 controller OCI
archives, image SBOMs and verification evidence. For a source build, use the
[local image instructions](docs/controller-local.md#build-the-oci-archives).
See [verify a release](docs/releases/VERIFYING.md) before importing an image
and [publish a release](docs/releases/PUBLISHING.md) for the maintainer process.

## Operate a deployment

- Use the [operator runbook](docs/operator-runbook.md) to prepare, apply, roll back, and decommission a deployment.
- Start with the [profile binding template](docs/profile-bindings.md) and check the runner identity, isolation, quota, and secret bindings.
- Configure exporters and monitor individual profiles with the [telemetry guide](docs/operator-telemetry.md).
- Record target validation against the [emulator and qualification boundaries](docs/emulator-boundaries.md).

## Build and test

Install the .NET SDK from `global.json` and Node.js from `.node-version`. Run
the build checks from the repository root.

```sh
make check
```

CI uses the same command to restore locked dependencies, build and test the .NET
solution in Release, check CDK linting, formatting, and types, run the CDK tests,
and synthesize the CDK baseline and matrix offline. The lifecycle and integration
tests start pinned MiniStack processes on loopback and require Python 3.11 or newer. To select a local
interpreter, run `make check MINISTACK_PYTHON=python3.11`.

The standalone `make check-integration` target also exercises the YAML emitter.
Run `npm --prefix cdk ci` first to install its pinned dependencies.

## Compile a configuration

Run `npm --prefix cdk ci` to install the pinned compiler dependencies. Use
the EC2 reference configuration to emit a runtime artifact.

```sh
npm --prefix cdk run config:emit -- ../testdata/config/ec2-only-latest.yaml /tmp/runner-runtime.json
```

The compiler prints the SHA-256 of the exact UTF-8 file it writes. It applies
`dry-run` mode and no demand source by default. This example configures a demand
snapshot and requests AWS inspection when reconciled. Specify pinned GitHub
scale-set IDs and EC2 launch-template versions as quoted positive decimal
strings to preserve their precision.

GitHub routing values retain their authored spelling. Scope, group, scale-set,
and label comparisons use the pinned Unicode 17.0 simple case-folding data.

## Run an offline capacity report

The pinned EC2 example uses no demand source and can run offline as-is.

```sh
npm --prefix cdk run config:emit -- ../testdata/config/ec2-only-pinned.yaml /tmp/runner-offline.json
dotnet run --project src/Knightfall.ScaleSet.Aws.Controller -- reconcile --config /tmp/runner-offline.json --dry-run --once
```

For local snapshots or replays, set the demand path relative to the runtime
JSON file or use an absolute path. The [replay fixture](testdata/dryrun/replay.json)
shows the observation format, including the deployment and GitHub profile
bindings. Replay evaluates observations in chronological order. The offline
command reports whether AWS inspection was requested and requires local
evidence files.
