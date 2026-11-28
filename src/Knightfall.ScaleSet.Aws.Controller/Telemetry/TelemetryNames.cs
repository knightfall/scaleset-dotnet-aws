using System.Collections.Frozen;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal static class TelemetryNames
{
    internal const string Controller = "Knightfall.ScaleSet.Aws.Controller";
    internal const string Client = "ScaleSet.Client";
    internal const string Listener = "ScaleSet.Listener";
    internal const string Http = "System.Net.Http";
    internal const string EventTemplate = "Telemetry operation {operation} finished with {outcome}";
    internal const string CredentialRejectionTemplate =
        "GitHub credential candidate rejected ({Error}, HTTP {StatusCode}, rate limit {RateLimitKind}, retry after {RetryAfter}, reset {Reset}, retries {Retries})";

    internal static readonly FrozenSet<string> Components = new[]
    {
        Controller,
        "Knightfall.ScaleSet.Aws.Core",
        "Knightfall.ScaleSet.Aws.DynamoDb",
        "Knightfall.ScaleSet.Aws.GitHub",
        "Knightfall.ScaleSet.Aws.Providers.Ec2",
        "Knightfall.ScaleSet.Aws.Providers.Fargate",
        "Knightfall.ScaleSet.Aws.Handoff",
        "Knightfall.ScaleSet.Aws.RunnerBootstrap",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly FrozenSet<string> Operations = new[]
    {
        "smoke", "reconcile", "plan", "listener-step", "poll", "delivery", "acquire", "checkpoint",
        "acknowledge", "settlement", "cooldown", "session-retire", "quarantine", "lease-acquire", "lease-renew",
        "lease-loss", "transaction", "effect-begin", "effect-resolve", "effect-rebuild", "inventory", "template-resolve",
        "template-drift", "launch", "readback", "az-failover", "terminate", "task-preflight", "replay", "stop",
        "offer-publish", "offer-consume", "offer-revoke", "handoff", "startup", "event-receive", "event-process", "event-delete",
        "capacity", "capacity_plan", "dry_run", "profile", "launch_template", "task_definition", "allocation_inventory",
        "run_instances", "run_instances_failover", "terminate_instances", "launch_readback", "termination_readback",
        "run_task", "stop_task", "stop_readback", "sqs_receive", "sqs_process", "sqs_delete",
        "step", "authorize", "checkpoint-read", "checkpoint-received", "checkpoint-processed", "checkpoint-acknowledging",
        "checkpoint-initialized", "checkpoint-settled", "admission-reserve", "admission-settle", "session-open", "session-close",
        "session-quarantine", "supervise", "effect-plan", "effect-uncertain", "effect-settle", "fargate-replay",
        "offer.publish", "offer.revoke", "offer.consume", "publish", "revoke", "consume", "advance", "seal", "open",
        "bootstrap-consume", "runner-listen", "credential-rotate",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly FrozenSet<string> Outcomes = new[]
    {
        "success", "failed", "unknown", "deferred", "cancelled", "cancelled-shutdown", "conflict", "rejected", "held",
        "ineligible", "duplicate", "timeout", "settled", "committed", "read-only", "unavailable", "other", "error",
        "initialized", "empty", "acknowledged", "received", "returned", "read", "fenced", "completed", "no_capacity",
        "reserved", "expired", "consumed", "not_available", "already_consumed",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static readonly FrozenSet<string> Reasons = new[]
    {
        "other", "none", "shutdown", "timeout", "ownership", "fence", "conflict", "stale", "invalid", "incomplete",
        "capacity", "rate-limit", "partial", "replay-cutoff", "ambiguous", "durable", "provider", "bootstrap", "duplicate",
        "out-of-order", "terminal", "expired", "unavailable", "drift", "busy", "unknown",
        "no_evidence", "cancellation", "profile_draining", "profile_disabled", "demand_unavailable", "inventory_unavailable",
        "capacity_requested", "headroom_unavailable", "target_met", "invalid_evidence", "template_verified", "template_unavailable",
        "template_version_changed", "template_incompatible", "aws_unavailable", "identity_mismatch", "role_mismatch",
        "external_reclamation_required", "status_qualification_mismatch", "ebs_key_unqualified",
        "predecessor_capture_verified",
        "network_incompatible", "inventory_complete", "inventory_incomplete", "insufficient_instance_capacity",
        "insufficient_subnet_addresses", "provider_rejected", "exact_observation", "task_definition_verified", "cluster_mismatch",
        "long_arns_unverified", "container_unapproved", "task_definition_incompatible", "no_exact_task", "no_exact_terminal_state",
        "exact_terminal_state", "batch_received", "queue_empty", "invalid_event", "readback_not_accepted", "readback_accepted",
        "acknowledged", "unowned_event", "duplicate_or_old", "rate_limit", "unauthorized", "replay", "cooldown", "held",
        "recovery_required", "journal_state", "incomplete_readback", "readback", "index_rebuild", "allocation_guard",
        "replay_window", "guarded_begin", "resend_authorized", "resend_suppressed", "ownership_unavailable", "identity_unavailable",
        "offer_confirmed", "registration_without_offer", "waiting_for_hello", "handoff_held", "handoff_finished",
        "provider_identity_verified", "interrupted", "already_spent", "hello_unconfirmed", "hello_rejected",
        "no_offer_before_deadline", "receipt_expired", "receipt_unknown", "binding_mismatch", "authentication_failed",
        "offer_authenticated", "listener_connected", "runner_exit_without_readiness",
        "credential_unavailable", "request_failed", "authorization_consumed",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static bool IsApplicationSpan(string name)
    {
        const string prefix = "scaleset.aws.";
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        string suffix = name[prefix.Length..];
        int delimiter = suffix.IndexOf('.');
        return delimiter > 0 && suffix[..delimiter] is "plan" or "reconcile" or "ec2" or "fargate" or "listener" or
            "github" or "dynamodb" or "handoff" or "startup" or "provider" or "telemetry" &&
            Operations.Contains(suffix[(delimiter + 1)..]);
    }

    internal static bool IsHttpMethod(string value) =>
        value is "GET" or "HEAD" or "POST" or "PUT" or "DELETE" or "CONNECT" or "OPTIONS" or "TRACE" or "PATCH" or "_OTHER";
}
