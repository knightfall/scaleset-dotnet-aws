# Agent instructions

- Prefer small, complete changes with clear names and straightforward control flow. Keep ownership, state transitions, and retry decisions explicit.
- Verify API and protocol contracts against the versions in use. Keep docs and examples aligned with verified behavior, and label assumptions.
- Use the [released SDK baseline](docs/dependency-baseline.md) for normal builds, CI, and release verification. Local SDK debugging may use temporary project references in an isolated checkout. Verify candidate compatibility with distinct package versions, a private feed, and a separate cache. Record the SDK commit and resolved versions. Keep temporary overrides out of commits.
- Keep Core free of SDK types and adapt them at the boundaries. Use validated, digest-pinned runner profiles and initialize only enabled EC2 or Fargate providers. The controller runs on ECS Fargate and stays independent of SDK Hosting and Docker. Follow the baseline for supported GitHub targets and TLS options.
- Make provisioning, handoff, and cleanup idempotent and lease-fenced. Preserve ambiguous outcomes and active or unknown runners. Use non-destructive CDK imports and keep credentials out of artifacts and diagnostics.
- Use `ScaleSetListenerPump` with application-owned checkpoints and policy. Retire only the exact session under current ownership and use a fresh checkpoint identity for its replacement.
- Use deterministic clocks and barriers for affected ownership, contention, cancellation, and deadline paths. Run the relevant `make check` targets and report what actually ran, including gaps.
