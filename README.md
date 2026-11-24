# scaleset-dotnet-aws

A planned .NET 10 controller for GitHub Actions runner scale sets on AWS,
using the released `Knightfall.ScaleSet.Client` and `Knightfall.ScaleSet.Listener`
packages. The controller will run on ECS Fargate and provision ephemeral runners
on Direct EC2 or standalone Fargate tasks.

The repository contains the planning baseline and build toolchain. The
controller and deployment infrastructure are tracked in the task index.

- [Implementation plan](docs/implementation-plan.md): architecture, configuration,
  lifecycle rules, and verification boundaries.
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
and synthesize the CDK baseline offline.
