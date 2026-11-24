using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Listener;

namespace Knightfall.ScaleSet.Aws.GitHub;

/// <summary>Persists the full Listener checkpoint for one exact owned GitHub session.</summary>
public sealed class GitHubListenerCheckpointStore : IScaleSetListenerCheckpointStore
{
    /// <summary>Leaves room under DynamoDB's 400 KiB item limit for keys and record attributes.</summary>
    private const int MaximumPayloadBytes = 384 * 1024;

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerOptions.Default)
    {
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly DynamoDbLifecycleStore _store;
    private readonly OwnedSessionFence _fence;
    private readonly LifecycleKey _key;
    private readonly TimeProvider _clock;

    public GitHubListenerCheckpointStore(DynamoDbLifecycleStore store, OwnedSessionFence fence, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fence);
        _store = store;
        _fence = fence;
        _clock = clock ?? TimeProvider.System;
        _key = LifecycleKey.Checkpoint(fence.Identity.Profile, fence.Identity.SessionId);
        SessionKey = new ScaleSetListenerSessionKey(fence.Identity.ScopeIdentity, fence.Identity.ScaleSetId,
            fence.Identity.SessionId);
    }

    public ScaleSetListenerSessionKey SessionKey { get; }

    public async Task<ScaleSetListenerCheckpoint?> ReadAsync(
        ScaleSetListenerSessionKey key, CancellationToken cancellationToken)
    {
        RequireSession(key);
        using var telemetry = GitHubListenerDiagnostics.Start("checkpoint-read", _clock, cancellationToken);
        var record = await _store.ReadRecordAsync(_key, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            telemetry.Complete("empty");
            return null;
        }
        if (record.Payload is not { } payload)
        {
            throw InvalidCheckpoint();
        }

        RequireBounded(payload);
        StoredCheckpoint? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<StoredCheckpoint>(payload, ReadOptions);
        }
        catch (JsonException)
        {
            throw InvalidCheckpoint();
        }

        if (snapshot?.Checkpoint is not { } checkpoint || checkpoint.Revision != record.Revision ||
            checkpoint.TransitionId != record.TransitionId)
        {
            throw InvalidCheckpoint();
        }
        Validate(checkpoint);
        if (snapshot.AcknowledgmentSha256 != AcknowledgmentSha256(checkpoint))
        {
            throw InvalidCheckpoint();
        }
        telemetry.Complete("read");
        return checkpoint;
    }

    public async Task<ScaleSetListenerCheckpointWriteResult> WriteAsync(
        ScaleSetListenerSessionKey key, long? expectedRevision, ScaleSetListenerCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        RequireSession(key);
        ArgumentNullException.ThrowIfNull(checkpoint);
        Validate(checkpoint);
        if (expectedRevision is <= 0 or long.MaxValue || checkpoint.Revision != (expectedRevision ?? 0) + 1)
        {
            throw InvalidCheckpoint();
        }

        // Preserve the exact SDK snapshot used when the pump compares an uncertain transition with readback.
        string payload = JsonSerializer.Serialize(new StoredCheckpoint(checkpoint, AcknowledgmentSha256(checkpoint)));
        RequireBounded(payload);
        string operation = checkpoint.Pending?.Phase switch
        {
            ScaleSetListenerPendingPhase.Received => "checkpoint-received",
            ScaleSetListenerPendingPhase.Processed => "checkpoint-processed",
            ScaleSetListenerPendingPhase.Acknowledging => "checkpoint-acknowledging",
            _ => checkpoint.LastAcknowledgedMessageId is null ? "checkpoint-initialized" : "checkpoint-settled",
        };
        using var telemetry = GitHubListenerDiagnostics.Start(operation, _clock, cancellationToken);
        var outcome = await _store.WriteSessionRecordsAsync(_fence, checkpoint.TransitionId,
            [new LifecycleRecordWrite(_key, expectedRevision, payload)], cancellationToken).ConfigureAwait(false);
        telemetry.Complete(GitHubListenerDiagnostics.WriteOutcome(outcome));
        return outcome switch
        {
            LifecycleWriteOutcome.Committed => ScaleSetListenerCheckpointWriteResult.Committed,
            LifecycleWriteOutcome.Conflict or LifecycleWriteOutcome.LeaseRejected => ScaleSetListenerCheckpointWriteResult.Conflict,
            LifecycleWriteOutcome.RebuildRequired => throw new IOException("The Listener checkpoint is waiting for the effect index to rebuild."),
            _ => throw new IOException("The Listener checkpoint write outcome is uncertain."),
        };
    }

    private void RequireSession(ScaleSetListenerSessionKey key)
    {
        if (key != SessionKey)
        {
            throw InvalidCheckpoint();
        }
    }

    private void Validate(ScaleSetListenerCheckpoint checkpoint)
    {
        if (checkpoint.SessionKey != SessionKey || checkpoint.SchemaVersion != ScaleSetListenerCheckpoint.CurrentSchemaVersion ||
            checkpoint.Revision <= 0 || checkpoint.TransitionId == Guid.Empty || checkpoint.LastAcknowledgedMessageId < 0)
        {
            throw InvalidCheckpoint();
        }

        if (checkpoint.Pending is { } pending &&
            (pending.Delivery is not { IsReplay: false } || pending.Delivery.SessionKey != SessionKey ||
             !Enum.IsDefined(pending.Phase)))
        {
            throw InvalidCheckpoint();
        }

        bool validPhase = checkpoint.InitialStatisticsCompleted
            ? checkpoint.Revision >= 2 && (checkpoint.Pending is null ||
                checkpoint.Pending.Delivery.Kind == ScaleSetListenerDeliveryKind.Message)
            : checkpoint.Revision == 1 && checkpoint.LastAcknowledgedMessageId is null &&
                checkpoint.Pending is { Phase: ScaleSetListenerPendingPhase.Received,
                    Delivery.Kind: ScaleSetListenerDeliveryKind.InitialStatistics };
        if (!validPhase)
        {
            throw InvalidCheckpoint();
        }
    }

    private static void RequireBounded(string payload)
    {
        if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The Listener checkpoint exceeds the durable payload limit.");
        }
    }

    private static string? AcknowledgmentSha256(ScaleSetListenerCheckpoint checkpoint)
    {
        if (checkpoint.Pending is not { Phase: ScaleSetListenerPendingPhase.Acknowledging, Delivery.Message: { } message })
        {
            return null;
        }

        var key = checkpoint.SessionKey;
        var request = new AcknowledgmentRequest(1, "delete-message", key.Scope, key.ScaleSetId, key.SessionId, message.MessageId);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
    }

    private static InvalidDataException InvalidCheckpoint() => new("Invalid durable Listener checkpoint or session identity.");

    private sealed record StoredCheckpoint(ScaleSetListenerCheckpoint Checkpoint, string? AcknowledgmentSha256);

    private sealed record AcknowledgmentRequest(int SchemaVersion, string Operation, string Scope, long ScaleSetId, Guid SessionId, long MessageId);
}
