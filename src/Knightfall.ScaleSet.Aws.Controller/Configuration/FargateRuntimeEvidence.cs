using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Providers.Fargate;

namespace Knightfall.ScaleSet.Aws.Controller.Configuration;

internal sealed class FargateRuntimeEvidence(
    FrozenDictionary<string, FargateContainerApproval> profiles, FargateQuotaEvidence? quota)
{
    internal const string EnvironmentVariable = "SCALESET_FARGATE_PREFLIGHT";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal FargateQuotaEvidence? Quota { get; } = quota;

    internal FargateContainerApproval? ForProfile(string profileId) => profiles.GetValueOrDefault(profileId);

    internal static FargateRuntimeEvidence Load(RuntimeConfiguration configuration, string? value)
    {
        if (value is null)
        {
            return new FargateRuntimeEvidence(FrozenDictionary<string, FargateContainerApproval>.Empty, null);
        }

        Payload? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<Payload>(value, Json);
        }
        catch (JsonException)
        {
            throw InvalidEvidence();
        }
        if (evidence?.Profiles is not { } approvals)
        {
            throw InvalidEvidence();
        }

        var enabled = configuration.Profiles.Where(profile => profile.State != RunnerProfileState.Disabled &&
            profile.Provisioner.Type == RunnerProvisionerType.Fargate).Select(profile => profile.Id).ToFrozenSet(StringComparer.Ordinal);
        if (approvals.Any(pair => !enabled.Contains(pair.Key) || pair.Value is not { } approval ||
            string.IsNullOrWhiteSpace(approval.Image) || approval.EntryPoint.IsDefault || approval.Command.IsDefault))
        {
            throw InvalidEvidence();
        }
        if (evidence.Quota is { } quota &&
            (quota.Account != configuration.Aws.Account || quota.Region != configuration.Aws.Region ||
             quota.ObservedAt == default || quota.ExpiresAt <= quota.ObservedAt || quota.RemainingCpuUnits < 0 || quota.ReservedCpuUnits < 0))
        {
            throw InvalidEvidence();
        }

        return new FargateRuntimeEvidence(approvals.ToFrozenDictionary(StringComparer.Ordinal), evidence.Quota);
    }

    private static ArgumentException InvalidEvidence() => new("Fargate preflight evidence is invalid for the configured deployment.");

    private sealed record Payload(Dictionary<string, FargateContainerApproval> Profiles, FargateQuotaEvidence? Quota);
}
