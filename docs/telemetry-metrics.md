# Controller metrics

Each controller exports one resource identity. Group profile measurements by deployment, profile, and provider, and keep the resource's `service.instance.id` available to distinguish controller replacements. A controller publishes live capacity only while it holds the matching active lease. After a lease ends it publishes readiness zero for configured profiles. A retired profile's observations disappear entirely. A missing sample means the controller lacks current evidence.

AWS instruments below use the `scaleset.aws.` prefix. Durations use seconds. The OTLP unit `{runner}` refers to a runner allocation or planner slot. `{job}` refers to GitHub-assigned jobs. The names of meters identify the owning assemblies. An instrument can be emitted by more than one assembly for distinct boundaries, such as an AWS request and its authoritative readback.

| Gauge | Unit | Evidence |
| --- | --- | --- |
| `capacity.assigned` | `{job}` | Fresh GitHub `TotalAssignedJobs` observation for the exact profile. |
| `capacity.active` | `{runner}` | Busy slots with verified ownership in complete inventory. |
| `capacity.idle` | `{runner}` | Planner-eligible serving slots minus busy slots in complete inventory. |
| `capacity.starting` | `{runner}` | Verified starting slots that are still within the profile's startup limit. |
| `capacity.unknown` | `{runner}` | Slots counted conservatively because ownership, current state, or timely startup could not be verified. |
| `capacity.deficit` | `{runner}` | Fresh planner target minus eligible serving and credible pending slots, floored at zero. |
| `capacity.drift` | `{runner}` | Count of allocation-definition discrepancies when the profile's provider can attest it from complete inventory. |
| `capacity.orphan_candidates` | `{runner}` | Slots whose ownership could not be verified. This evidence does not authorize cleanup. |
| `capacity.configured_max`, `capacity.configured_idle_reserve` | `{runner}` | Configured profile limits. The planner can further reduce the eligible capacity. |
| `inventory.age` | `s` | Age of the most recent complete inventory in the current ownership epoch. |
| `reconcile.age` | `s` | Age of the latest completed reconciliation published by that owner. |
| `cleanup.oldest_age` | `s` | Age of the oldest pending cleanup for which the controller has current eligibility evidence. |
| `effects.unfinished` | `{operation}` | Current unresolved effects from a verified active-effect index. |
| `listener.cooldown.remaining` | `s` | Remaining time after a scoped cooldown observation. Zero records an expired or cleared cooldown. |
| `profile.ready` | `1` | One for authoritative current readiness, zero when an enabled profile lacks that readiness. |

An inventory-related capacity series disappears when evidence becomes incomplete or stale. `inventory.age` can still show the age of the last complete observation until ownership ends. Assigned jobs and inventory have separate freshness checks. Zero counts are valid when the corresponding evidence is complete. Valid profile names up to 256 characters appear as configured. Longer names retain their first 191 characters, a colon, and the lowercase SHA-256 of the full name.

| Counter or histogram | Unit | Measurement boundary |
| --- | --- | --- |
| `reconcile.duration` | `s` | One completed profile reconciliation. The `dry_run` operation identifies offline profile planning. |
| `listener.operations`, `listener.operation.duration` | `{operation}`, `s` | Scope-specific poll, acknowledgement, delivery, checkpoint, and supervision work. A successful wire acknowledgement is separate from durable settlement. |
| `provider.operations`, `provider.operation.duration` | `{operation}`, `s` | Each typed operation at the EC2 or ECS provider or event queue. Outer attempts and child readback operations are separate observations. Filter by `operation` when counting AWS mutation attempts. |
| `event.age` | `s` | Time since the parsed EventBridge timestamp of a scoped queue hint. Dates in the future or more than 14 days old are omitted. Fargate attributes a hint to a profile only after exact task readback. |
| `lease.losses` | `{operation}` | A controller loses active authority. Normal voluntary release is outside this counter. |
| `dynamodb.conflicts` | `{operation}` | A transaction returns a typed conflict. The transaction's items and keys are excluded. |
| `effect.rebuild.pages` | `{page}` | Pages actually read by a bounded active-effect rebuild pass. |
| `ec2.az_failovers` | `{operation}` | A launch is attempted for an alternate AZ intent after the first intent's definite rejection. |
| `ec2.template_drifts` | `{operation}` | Preflight finds an incompatible alias target, or exact send preflight observes a changed alias version. Multiple checks may observe the same drift. |
| `handoff.operations`, `handoff.duration` | `{operation}`, `s` | Controller or bootstrap handoff boundary named by the operation. Publication, consumption, and revocation are separately observed. |
| `startup.duration` | `s` | Time to the startup evidence observed by the named operation. Provider task state alone does not establish runner readiness or GitHub completion. |

AWS metric dimensions are drawn from `profile`, `provider`, `operation`, `outcome`, and `reason`. The released Client retains its own `dependency`, `method`, `status_class`, and `retry_kind` dimensions. Use the AWS listener meter for profile breakdown. The released Listener provides spans and has no meter.
