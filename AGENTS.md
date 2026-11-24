# Agent Instructions

## Principles

- Read existing code before modifying it.
- Prefer simple, idiomatic solutions and minimal diffs.
- No speculative abstractions, unnecessary dependencies, or unrelated refactoring.
- No placeholder implementations, redundant comments, or generated boilerplate.
- Verify external SDK APIs (`Knightfall.ScaleSet.*` and AWS SDK for .NET v4); never invent functionality.

## Architecture

- Target `.NET 10` (`net10.0`) using the published `Knightfall.ScaleSet.Client` and `Knightfall.ScaleSet.Listener` `0.1.0-alpha.1` packages plus AWS SDK for .NET v4. Follow [the released dependency baseline](docs/dependency-baseline.md); do not substitute same-version candidate packages or project references.
- Do not depend on `Knightfall.ScaleSet.Providers.Docker` or local-file `FileRunnerStateJournal` in `Knightfall.ScaleSet.Hosting`.
- Keep `Knightfall.ScaleSet.Aws.Core` independent of AWS SDK and GitHub SDK types. Keep SDK calls and request/observation translation in provider, persistence, and GitHub adapters.
- Keep runner profiles configuration-driven through validated YAML compiled to a canonical digest-pinned JSON artifact.
- Initialise only enabled AWS provisioners (`ec2`, `fargate`). Controller hosting is ECS Fargate only (`dotnet publish /t:PublishContainer`); anything related to Docker runner provisioning or Docker controller hosting is out of scope.
- Keep custom GHES hosts (`scaleset.NET` `#20`) and custom root CA / mTLS (`scaleset.NET` `#21`) explicitly deferred and rejected during validation until implemented upstream.
- Reuse existing AWS infrastructure via non-destructive CDK imports; never terminate active or unknown runners or expose credentials.
- Ensure provisioning, JIT handoff, and cleanup are idempotent, lease-fenced, and concurrency-safe.
- Reuse `ScaleSetListenerPump` with application-owned checkpoint, handler, and policy adapters. Keep session supervision separate: use `DeleteMessageSessionAsync` for exact-session retirement after process loss only under current ownership, preserve ambiguous outcomes, and give replacement sessions fresh checkpoint identities.

## Repository Layout

- Use the `Knightfall.ScaleSet.Aws.*` prefix for all `.NET` projects, assemblies, namespaces, and packages to align with `scaleset.NET`'s `Knightfall.ScaleSet.*` family without colliding with its `ScaleSet.*` namespaces.
- Keep the solution in `Knightfall.ScaleSet.Aws.slnx` with `global.json`, `Directory.Build.props`, `Directory.Packages.props`, and `.editorconfig` at the repository root.
- Keep production projects under `src/`: `Knightfall.ScaleSet.Aws.Core`, `Knightfall.ScaleSet.Aws.DynamoDb`, `Knightfall.ScaleSet.Aws.GitHub`, `Knightfall.ScaleSet.Aws.Providers.Ec2`, `Knightfall.ScaleSet.Aws.Providers.Fargate`, `Knightfall.ScaleSet.Aws.Handoff`, `Knightfall.ScaleSet.Aws.Controller`, and `Knightfall.ScaleSet.Aws.RunnerBootstrap`.
- Keep black-box contract, lifecycle, provider, and local emulated AWS/GitHub integration suites under `tests/`: `Knightfall.ScaleSet.Aws.SdkContract.Tests`, `Knightfall.ScaleSet.Aws.Core.Tests`, `Knightfall.ScaleSet.Aws.Lifecycle.Tests`, `Knightfall.ScaleSet.Aws.Providers.Tests`, and `Knightfall.ScaleSet.Aws.IntegrationTests` (backed by MiniStack `ministackorg/ministack` or AWS Emulator `aws/aws-emulator` via `AWS_ENDPOINT_URL`, loopback IMDSv2/ECS metadata stubs, and a local GitHub Scale Set simulator — no live cloud credentials required). Keep white-box tests using `InternalsVisibleTo` only when testing unexported invariants is necessary.
- Keep CDK entry points in `cdk/bin/`, stack constructs and config compiler in `cdk/lib/`, schemas in `cdk/config/schema/`, and TypeScript tests in `cdk/test/`.
- Keep shared YAML, runtime JSON, and dry-run fixtures in `testdata/config/` and `testdata/dryrun/`.

## Code Quality

- **.NET 10:** `dotnet restore Knightfall.ScaleSet.Aws.slnx --locked-mode`, `dotnet build Knightfall.ScaleSet.Aws.slnx --configuration Release --no-restore` (`TreatWarningsAsErrors=true`, `Nullable=enable`, `AnalysisLevel=latest-recommended`, `Roslynator.Analyzers` pinned centrally with `PrivateAssets="all"`), and `dotnet test --solution Knightfall.ScaleSet.Aws.slnx --configuration Release --no-build` (`xunit.v3` on Microsoft Testing Platform).
- **TypeScript/CDK:** strict TypeScript, ESLint, Prettier, `tsc --noEmit`, `cdk synth --strict --no-lookups`.
- Handle errors explicitly; never suppress compiler, `.NET`, or Roslynator diagnostics without a narrow, documented contract justification.
- Use `TimeProvider` for clocks/delays, pass `CancellationToken` on async boundaries, avoid `Task.Run` around synchronous I/O, and use guard clauses to keep control flow shallow.
- Write meaningful tests for changed behaviour using fake transports, deterministic clocks, and wire fixtures.
- Pin SDK, NuGet, npm, and container base versions and run the exact same checks in CI.

## Clarity and Simplicity

- Do not optimise for syntactic cleverness. Prefer familiar, easily reviewable constructs when two implementations are otherwise equivalent. Replace readable code with shorter alternatives only when they materially improve clarity.
- Use modern, idiomatic C# (records, pattern matching, `readonly struct` where helpful) when it improves clarity, not merely because a construct is shorter or newer.
- Prefer immutable values by default. Preserve zero versus absent versus unknown explicitly via typed states or intentional nullability rather than sentinel values.
- Prefer straightforward loops, explicit state machines, and early returns. Split dense conditions when they express distinct invariants or need actionable errors.
- Extract types or methods for clearer boundaries and testability, not arbitrary line limits. Add interfaces only for real adapter seams or test isolation.
- Keep lifecycle transitions, ownership fencing, concurrency, retries, and recovery explicit and auditable.
- Explain non-obvious constraints, not what the code already says. Avoid cosmetic refactors and unnecessary code churn.
- Before finishing, check whether another engineer can follow both the normal path and failure cases without reconstructing hidden control flow.

## Delivery

- This is a planning baseline until T01 creates the solution and CI. Commands and layouts above describe the intended implementation; validate documentation changes with link, task/dependency, and whitespace checks while no build exists.
- Keep task specifications in `docs/`; use the later T01–T26 issues to track dependencies, PRs, and verification rather than duplicating the specification text.
- Make the smallest complete, testable change.
- Run relevant checks (`make check` or matching `.NET` and CDK suites) before declaring completion.
- Report changes, validation results, and unresolved limitations.
- Never claim checks passed unless executed.
