using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Planning;

namespace Knightfall.ScaleSet.Aws.Core.DryRun;

internal static class DryRunEvidence
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];
    private static readonly JsonSerializerOptions Serializer = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<RunnerSlotState>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    internal static ImmutableArray<PlanningObservation> Read(
        RuntimeConfiguration configuration,
        ReadOnlyMemory<byte> evidence,
        bool replay)
    {
        byte[] bytes = evidence.ToArray();
        if (configuration.DryRun.Demand is S3SnapshotDemandConfiguration snapshot &&
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new DryRunEvidenceException("The demand snapshot SHA-256 does not match the configured digest.");
        }

        Artifact? artifact;
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            artifact = JsonSerializer.Deserialize<Artifact>(bytes, Serializer);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            throw new DryRunEvidenceException("Demand evidence must be valid UTF-8 JSON with unique, recognized properties and values.");
        }

        string kind = replay ? "RunnerDemandReplay" : "RunnerDemandSnapshot";
        if (artifact is null || artifact.ApiVersion != "scaleset-aws/v1alpha1" || artifact.Kind != kind ||
            artifact.Environment != configuration.EnvironmentName || artifact.Aws != configuration.Aws ||
            artifact.Records.IsDefault || (replay && artifact.Records.IsEmpty))
        {
            throw new DryRunEvidenceException("Demand evidence must identify the configured deployment and the selected v1alpha1 source kind.");
        }

        var profiles = configuration.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var snapshotProfiles = new HashSet<string>(StringComparer.Ordinal);
        var observedIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<PlanningObservation>(artifact.Records.Length);
        foreach (Record? record in artifact.Records)
        {
            if (record is null || !profiles.TryGetValue(record.ProfileId, out RunnerProfile? profile) ||
                (record.Demand is null && record.Inventory is null))
            {
                throw new DryRunEvidenceException("Each evidence record must provide a demand or inventory observation for a configured profile.");
            }
            if (!replay && !snapshotProfiles.Add(record.ProfileId))
            {
                throw new DryRunEvidenceException("A demand snapshot may contain one record per profile.");
            }

            RequireBinding(configuration.GitHub[profile.GitHub.Connection], profile, record, observedIds);
            DateTimeOffset observedAt = ReadTimestamp(record.ObservedAt);
            DemandSample? demand = record.Demand is null ? null : new DemandSample(observedAt, record.Demand.TotalAssignedJobs);
            InventorySample? inventory = record.Inventory is null ? null : ToInventory(record.Inventory, observedAt);
            result.Add(new PlanningObservation(profile.Id, observedAt, demand, inventory));
        }
        return result.MoveToImmutable();
    }

    private static InventorySample ToInventory(Inventory inventory, DateTimeOffset observedAt)
    {
        if (inventory.Slots.IsDefault || inventory.Slots.Any(slot => slot is null))
        {
            throw new DryRunEvidenceException("Inventory evidence must contain a slot array with no null entries.");
        }
        var slots = inventory.Slots.Select(slot => new RunnerSlot(
            slot!.Id, slot.State, slot.OwnershipVerified,
            slot.StartedAt is string startedAt ? ReadTimestamp(startedAt) : null)).ToImmutableArray();
        return new InventorySample(observedAt, inventory.IsComplete,
            inventory.BackendCapacityCeiling, inventory.BackendLaunchHeadroom, slots);
    }

    private static DateTimeOffset ReadTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset timestamp))
        {
            throw new DryRunEvidenceException("Use an ISO 8601 timestamp with an explicit UTC offset in demand evidence.");
        }
        return timestamp;
    }

    private static void RequireBinding(
        GitHubConnectionConfiguration connection,
        RunnerProfile profile,
        Record record,
        Dictionary<string, long> observedIds)
    {
        var expected = RuntimeConfigurationSemantics.GitHubBinding(
            connection.ScopeUrl, profile.GitHub.RunnerGroup, profile.GitHub.ScaleSet, "");
        (string Scope, string Group, string ScaleSet) observed;
        try
        {
            observed = RuntimeConfigurationSemantics.GitHubBinding(
                record.ScopeUrl, record.RunnerGroup, record.ScaleSet, "");
        }
        catch (RuntimeConfigurationException)
        {
            throw new DryRunEvidenceException("An evidence record contains an invalid GitHub scope.");
        }

        long? scaleSetId = null;
        if (record.ScaleSetId is string text)
        {
            if (text.Length == 0 || text[0] == '0' ||
                !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed <= 0)
            {
                throw new DryRunEvidenceException("An evidence scale-set ID must be a positive signed 64-bit decimal string.");
            }
            scaleSetId = parsed;
        }
        if (observed != expected || (profile.GitHub.ScaleSetId is long pinned && scaleSetId != pinned))
        {
            throw new DryRunEvidenceException("An evidence record does not match its configured GitHub profile binding.");
        }
        if (scaleSetId is long id && !observedIds.TryAdd(profile.Id, id) && observedIds[profile.Id] != id)
        {
            throw new DryRunEvidenceException("Evidence for one profile cannot combine distinct scale-set IDs.");
        }
    }

    private sealed record Artifact(
        string ApiVersion, string Kind, string Environment, AwsEnvironment Aws, ImmutableArray<Record?> Records);

    private sealed record Record(
        string ProfileId,
        [property: JsonPropertyName("scopeURL")] string ScopeUrl,
        string RunnerGroup,
        string ScaleSet,
        string ObservedAt,
        string? ScaleSetId = null,
        Demand? Demand = null,
        Inventory? Inventory = null);

    private sealed record Demand(int TotalAssignedJobs);

    private sealed record Inventory(
        bool IsComplete,
        ImmutableArray<Slot?> Slots,
        int? BackendCapacityCeiling = null,
        int? BackendLaunchHeadroom = null);

    private sealed record Slot(string Id, RunnerSlotState State, bool OwnershipVerified, string? StartedAt = null);
}

internal sealed record PlanningObservation(
    string ProfileId, DateTimeOffset ObservedAt, DemandSample? Demand, InventorySample? Inventory);
