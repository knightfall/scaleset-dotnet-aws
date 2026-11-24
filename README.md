# scaleset-dotnet-aws

A planned .NET 10 controller for GitHub Actions runner scale sets on AWS,
using the released `Knightfall.ScaleSet.Client` and `Knightfall.ScaleSet.Listener`
packages. The controller will run on ECS Fargate and provision ephemeral runners
on Direct EC2 or standalone Fargate tasks.

The repository contains the planning baseline and build toolchain. The
controller and deployment infrastructure are tracked in the task index.

- [Implementation plan](docs/implementation-plan.md): architecture, configuration,
  lifecycle rules, and verification boundaries.
- [OpenTelemetry integration plan](docs/opentelemetry-integration.md): signal
  contracts, per-profile visibility, OTLP configuration, and acceptance evidence.
- [Released SDK baseline](docs/dependency-baseline.md): exact package version,
  release evidence, supported Listener APIs, and session-recovery limits.
- [Task index](docs/tasks/README.md): 26 tasks across six milestones, with
  dependencies, deliverables, and acceptance criteria.
- [Agent instructions](AGENTS.md): repository layout and engineering conventions.

## Build and test

Install the .NET SDK from `global.json` and Node.js from `.node-version`. Run
the build checks from the repository root.

```sh
make check
```

CI uses the same command to restore locked dependencies, build and test the .NET
solution in Release, check CDK linting, formatting, and types, run the CDK tests,
and synthesize the CDK baseline offline. The lifecycle tests run against a pinned
MiniStack process on loopback and require Python 3.11 or newer. To select a local
interpreter, run `make check MINISTACK_PYTHON=python3.11`.

## Compile a configuration

Run `npm --prefix cdk ci` to install the pinned compiler dependencies. Use
the EC2 reference configuration to emit a runtime artifact.

```sh
npm --prefix cdk run config:emit -- ../testdata/config/ec2-only-latest.yaml /tmp/runner-runtime.json
```

The compiler prints the SHA-256 of the exact UTF-8 file it writes. It applies
`dry-run` mode and no demand source by default. Specify pinned GitHub
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
