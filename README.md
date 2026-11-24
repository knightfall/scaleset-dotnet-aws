# scaleset-dotnet-aws

A planned .NET 10 controller for GitHub Actions runner scale sets on AWS,
using the released `Knightfall.ScaleSet.Client` and `Knightfall.ScaleSet.Listener`
packages. The controller will run on ECS Fargate and provision ephemeral runners
on Direct EC2 or standalone Fargate tasks.

This repository currently contains the planning baseline. The controller,
infrastructure, build commands, and tests described below are implementation
work, not available functionality.

- [Implementation plan](docs/implementation-plan.md): architecture, configuration,
  lifecycle rules, and verification boundaries.
- [Released SDK baseline](docs/dependency-baseline.md): exact package version,
  release evidence, supported Listener APIs, and session-recovery limits.
- [Task index](docs/tasks/README.md): 26 tasks across six milestones, with
  dependencies, deliverables, and acceptance criteria.
- [Agent instructions](AGENTS.md): repository layout and engineering conventions.

Merge the planning-baseline PR before creating the milestone and task issues.
Keep detailed requirements in these documents and use issues to track
dependencies, implementation PRs, and verification evidence. Begin with T01
(tooling) and T02 (released SDK contracts).
