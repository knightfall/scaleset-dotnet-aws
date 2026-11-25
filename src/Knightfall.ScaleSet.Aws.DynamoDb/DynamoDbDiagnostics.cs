using System.Diagnostics;
using System.Diagnostics.Metrics;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

internal static class DynamoDbDiagnostics
{
    private const string Name = "Knightfall.ScaleSet.Aws.DynamoDb";
    private static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Counter<long> Conflicts = Meter.CreateCounter<long>("scaleset.aws.dynamodb.conflicts", "{operation}");
    private static readonly Counter<long> LeaseLosses = Meter.CreateCounter<long>("scaleset.aws.lease.losses", "{operation}");
    private static readonly Counter<long> RebuildPages = Meter.CreateCounter<long>("scaleset.aws.effect.rebuild.pages", "{page}");
    private static readonly Histogram<double> HandoffDuration = Meter.CreateHistogram<double>("scaleset.aws.handoff.duration", "s");
    private static readonly Counter<long> HandoffOperations = Meter.CreateCounter<long>("scaleset.aws.handoff.operations", "{operation}");

    internal static Observation Start(string operation, CancellationToken cancellationToken) =>
        new(operation, TelemetryProfileScope.Current, null, cancellationToken);

    internal static Observation StartLease(string operation, CancellationToken cancellationToken) =>
        new(operation, null, null, cancellationToken);

    internal static Observation StartHandoff(string operation, TimeProvider clock) =>
        new(operation, TelemetryProfileScope.Current, clock, default);

    internal static void RecordLeaseLoss() => LeaseLosses.Add(1);

    internal static void RecordConflict(TelemetryProfile? profile)
    {
        var tags = profile?.Tags() ?? new TagList();
        tags.Add("operation", "transaction");
        tags.Add("outcome", "conflict");
        Conflicts.Add(1, tags);
    }

    internal static void RecordRebuild(EffectRebuildPass result)
    {
        if (result.PagesRead <= 0)
        {
            return;
        }
        var tags = TelemetryProfileScope.Current?.Tags() ?? new TagList();
        tags.Add("operation", "effect-rebuild");
        tags.Add("outcome", Outcome(result.Outcome));
        RebuildPages.Add(result.PagesRead, tags);
    }

    private static string Outcome(LifecycleWriteOutcome status) => status switch
    {
        LifecycleWriteOutcome.Committed => "committed",
        LifecycleWriteOutcome.Conflict => "conflict",
        LifecycleWriteOutcome.LeaseRejected => "fenced",
        LifecycleWriteOutcome.RebuildRequired => "deferred",
        _ => "unknown",
    };

    internal sealed class Observation : IDisposable
    {
        private readonly Activity? _activity;
        private readonly CancellationToken _cancellationToken;
        private readonly TelemetryProfile? _profile;
        private readonly string _operation;
        private readonly TimeProvider? _clock;
        private readonly long _started;
        private string? _outcome;
        private string? _reason;

        internal Observation(string operation, TelemetryProfile? profile, TimeProvider? clock, CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationToken = cancellationToken;
            _profile = profile;
            _clock = clock;
            _started = clock?.GetTimestamp() ?? 0;
            _activity = Source.StartActivity((clock is null ? "scaleset.aws.dynamodb." : "scaleset.aws.handoff.") + operation);
            profile?.SetTags(_activity);
            _activity?.SetTag("operation", operation);
        }

        internal LifecycleWriteOutcome Complete(LifecycleWriteOutcome status)
        {
            _outcome = Outcome(status);
            if (_operation == "transaction" && status == LifecycleWriteOutcome.Conflict)
            {
                RecordConflict(_profile);
            }
            return status;
        }

        internal ControllerLeaseChange Complete(ControllerLeaseChange result)
        {
            Complete(result.Outcome);
            return result;
        }

        internal JitHandoffChange Complete(JitHandoffChange result)
        {
            Complete(result.Outcome);
            return result;
        }

        internal EffectDispatchStatus Complete(EffectDispatchStatus result, string reason)
        {
            _outcome = result switch
            {
                EffectDispatchStatus.Settled => "settled",
                EffectDispatchStatus.OwnershipUnavailable => "fenced",
                EffectDispatchStatus.Conflict => "conflict",
                EffectDispatchStatus.AwaitingEvidence => "unknown",
                _ => "deferred",
            };
            _reason = reason;
            return result;
        }

        internal BootstrapOfferReceipt Complete(BootstrapOfferReceipt result)
        {
            _outcome = result switch
            {
                { Expired: true } => "expired",
                { Status: BootstrapOfferStatus.Consumed } => "consumed",
                { Status: BootstrapOfferStatus.NotAvailable } => "not_available",
                { Status: BootstrapOfferStatus.AlreadyConsumed } => "already_consumed",
                _ => "unknown",
            };
            return result;
        }

        internal void CompleteStatusRead(string outcome) => _outcome = outcome;

        public void Dispose()
        {
            string outcome = _outcome ?? (_cancellationToken.IsCancellationRequested ? "cancelled" : "unknown");
            _activity?.SetTag("outcome", outcome);
            if (_reason is not null)
            {
                _activity?.SetTag("reason", _reason);
            }
            if (_clock is not null)
            {
                var tags = _profile?.Tags() ?? new TagList();
                tags.Add("operation", _operation);
                tags.Add("outcome", outcome);
                HandoffOperations.Add(1, tags);
                HandoffDuration.Record(_clock.GetElapsedTime(_started).TotalSeconds, tags);
            }
            _activity?.Dispose();
        }
    }
}
