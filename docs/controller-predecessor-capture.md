# Controller predecessor capture

The controller normally fences an unclean predecessor with an exact ECS
`DescribeTasks` result. If ECS returns a sole `MISSING` failure for that task,
the active controller can optionally check a prequalified CloudWatch Logs
capture. Configure and qualify the capture while the original hosting scope is
available. ECS keeps stopped tasks in `DescribeTasks` for at least one hour.
[ECS DescribeTasks](https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DescribeTasks.html)

## Qualify the capture before enabling it

1. Use an existing dedicated log group and default-bus EventBridge rule in the
   deployment account and Region. Restrict the rule to native `aws.ecs` `ECS
   Task State Change` events for the original controller cluster, Fargate, and
   actual `lastStatus: STOPPED`. Exclude events with `replay-name`. Use the full
   unmodified EventBridge event as the CloudWatch Logs target input. Capture a
   test STOPPED event on the target and verify its exact task ARN, cluster ARN,
   `createdAt`, `stoppedAt` and EventBridge envelope. Keep retention long enough
   for the recovery interval.
   [ECS task events](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs_task_events.html) · [ECS Task response](https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_Task.html)
2. Have the AWS security owner verify the original event path, target, group
   creation record, rule configuration, effective service delivery policy and
   effective identity-based policies. Demonstrate that the controller task role,
   controller task execution role, runner roles and other workload principals
   cannot create streams or write events to this group. Account for any broader
   same-account policies and all other delivery paths. CloudWatch Logs resource
   policies govern AWS services. Identity policies govern same-account IAM
   principals. Keep exclusive ownership of rule, target, group, policy, and
   retention changes with the qualified security administrators.
   [CloudWatch Logs resource policies](https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_PutResourcePolicy.html) · [CloudWatch Logs identity policies](https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/iam-identity-based-access-control-cwl.html)
3. Record the exact rule and group ARNs and the independently reviewed
   qualification start and expiry, including who verified continuous write
   isolation. Retain the rule pattern, raw target configuration, policy
   evidence, group creation identity, and audit references with the protected
   deployment. A group or rule can be recreated under the same name. If any
   protected resource is changed or continuity becomes uncertain, have the
   AWS security owner withdraw the capture read permission immediately. Hold
   missing-predecessor recovery until the binding is qualified again. The read
   API returns stored events and does not authenticate individual writers or
   report rule or group continuity.
   [FilterLogEvents response](https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_FilterLogEvents.html)

Pin the qualified binding in the deployment YAML. The protected deployment
and packaged controller read the same digest-pinned runtime. These are example
identifiers and timestamps.

```yaml
spec:
  controller:
    predecessorCapture:
      logGroupArn: arn:aws:logs:ap-southeast-2:111122223333:log-group:/aws/events/controller-fence
      eventRuleArn: arn:aws:events:ap-southeast-2:111122223333:rule/controller-fence
      qualifiedFrom: '2026-11-25T00:00:00Z'
      qualifiedUntil: '2026-12-02T00:00:00Z'
```

The controller task role receives only `logs:FilterLogEvents` on the named
group. Dry-run and configurations without capture keep the previous IAM
scope. CDK leaves capture creation and write policy with the operator. A live
deployment keeps the same capture binding, including the qualification window.
To add or change one on an existing deployment, use the normal profile
retirement path and move through a verified dry-run generation with no
unclean lease. Prequalify and review the new binding before the next active
generation. [Controller operator runbook](operator-runbook.md)

## Recover and verify

Record the exact lease and prior task identities using the
[unavailable-predecessor procedure](operator-runbook.md#isolate-a-predecessor-whose-ecs-task-is-unavailable).
When the installed stack has the qualified capture binding, leave the exact
lease, journal and real GitHub sessions in place and let the recovery-only
controller perform the lookup. Only a paginated, complete read of an exact
native `STOPPED` event inside the qualified interval and after the predecessor's
recorded lease acquisition permits the recovery to advance. An unavailable
capture, ambiguous query, wrong cluster, conflicting event, unknown task
response or expired qualification keeps the fence closed. Confirm authoritative
effect settlement and exact prior-session retirement before accepting the new
listener and its fresh cursor. The lookup adds no recovery write authority.
Structured controller logs report `predecessor_capture_verified` with the first
16 hexadecimal characters of SHA-256 over the canonical lowercase EventBridge
event ID. Match that reference against the independently retained event record.

Alpha.1 rows lack the acquisition-time binding needed for archived evidence;
they still recover through direct ECS `STOPPED` or clean release. Enable the
capture before the generations that will rely on it. For a legacy or uncaptured
predecessor that ECS can no longer describe, hold the deployment and use the
separately reviewed recovery procedure. The target qualification establishes
exclusive capture provenance and effective IAM. The repository tests exercise
the read and restart paths against local AWS and GitHub fixtures.
