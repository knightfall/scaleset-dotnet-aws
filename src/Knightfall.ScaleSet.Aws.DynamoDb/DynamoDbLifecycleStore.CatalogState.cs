using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public sealed partial class DynamoDbLifecycleStore
{
    private const string CatalogPublished = "catalogPublished";
    private const string CatalogCandidate = "catalogCandidate";
    private const string CatalogClaimPrefix = "catalog-v1#";

    public async Task<CatalogRolloutHead?> ReadCatalogRolloutAsync(string deployment, CancellationToken cancellationToken)
    {
        var item = await ReadItemAsync(LifecycleKey.Lease(deployment), cancellationToken).ConfigureAwait(false);
        if (item?.ContainsKey("catalogHead") != true)
        {
            return null;
        }

        var head = CatalogDeserialize<CatalogRolloutHead>(Text(item, "catalogHead"));
        if (head.Deployment != deployment || head.Revision <= 0 || head.Revision != Number(item, "catalogRevision") ||
            head.AppliedEpoch != Number(item, "catalogEpoch") || head.AppliedGeneration != Number(item, "catalogGeneration") ||
            (head.AppliedGeneration == 0 ? head.AppliedDigest is not null || head.AppliedEpoch != 0 :
                head.AppliedGeneration < 0 || head.AppliedEpoch <= 0 || !IsCatalogDigest(head.AppliedDigest)) ||
            item.ContainsKey("catalogPending") != (head.Pending is not null))
        {
            throw InvalidRecord();
        }
        if (head.Pending is { } pending && (pending.Generation <= head.AppliedGeneration ||
            !IsCatalogDigest(pending.Digest) || !IsCatalogDigest(pending.PlanDigest) ||
            pending.ActionCount <= 0 || pending.NextAction < 0 || pending.NextAction > pending.ActionCount))
        {
            throw InvalidRecord();
        }
        return head;
    }

    public async Task<CatalogProfile?> ReadCatalogProfileAsync(LifecycleProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var head = await ReadCatalogRolloutAsync(profile.Deployment, cancellationToken).ConfigureAwait(false);
        var item = await ReadItemAsync(LifecycleKey.Profile(profile), cancellationToken).ConfigureAwait(false);
        var after = await ReadCatalogRolloutAsync(profile.Deployment, cancellationToken).ConfigureAwait(false);
        if (head != after)
        {
            throw new IOException("The rollout changed while reading the profile.");
        }
        if (item is null || head is null)
        {
            return null;
        }
        return DecodeCatalogRow(profile, item).Effective(head);
    }

    private async Task<Dictionary<string, CatalogRow>> ReadCatalogRowsAsync(string deployment, CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, CatalogRow>(StringComparer.Ordinal);
        string partition = LifecycleKey.Lease(deployment).PartitionKey;
        string? cursor = null;
        do
        {
            var page = await ReadBasePageAsync(partition, CatalogProfilePrefix, cursor, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Items)
            {
                var profile = DecodeEffectProfile(item, deployment, partition);
                rows.Add(profile.ProfileId, DecodeCatalogRow(profile, item));
            }
            cursor = page.Continuation;
        }
        while (cursor is not null);
        return rows;
    }

    private static CatalogRow DecodeCatalogRow(LifecycleProfile profile, Dictionary<string, AttributeValue> item)
    {
        var key = LifecycleKey.Profile(profile);
        _ = DecodeEffectProfile(item, profile.Deployment, key.PartitionKey);
        var published = item.ContainsKey(CatalogPublished) ? CatalogDeserialize<CatalogProfileData>(Text(item, CatalogPublished)) : null;
        var candidate = item.ContainsKey(CatalogCandidate) ? CatalogDeserialize<StagedCatalogProfile>(Text(item, CatalogCandidate)) : null;
        if ((published is not null && !ValidProfileData(published)) ||
            (candidate is not null && (candidate.Generation <= 0 || !IsCatalogDigest(candidate.Digest) || !ValidProfileData(candidate.Data))))
        {
            throw InvalidRecord();
        }
        return new CatalogRow(profile, Revision(item), GuidValue(item, "transitionId"), Text(item, "payload"), published, candidate);
    }

    private TransactionAction WriteCatalogRow(
        LifecycleProfile profile, CatalogRow? previous, CatalogProfileData? published, StagedCatalogProfile? candidate, Guid transition)
    {
        var key = LifecycleKey.Profile(profile);
        var item = Record(key, profile.Deployment, NextRevision(previous?.Revision), transition);
        item.Add(CatalogProfileIdentity, StringValue(profile.ProfileId));
        item.Add("payload", StringValue(previous?.Payload ?? "{}"));
        if (published is not null)
        {
            item.Add(CatalogPublished, StringValue(JsonSerializer.Serialize(published)));
        }
        if (candidate is not null)
        {
            item.Add(CatalogCandidate, StringValue(JsonSerializer.Serialize(candidate)));
        }
        return CompareAndPut(key, item, previous?.Revision, profile.Deployment);
    }

    private TransactionAction WriteCatalogHead(
        DeploymentLeaseFence lease, CatalogRolloutHead? expected, CatalogRolloutHead replacement)
    {
        var auth = LeaseCondition(lease);
        var check = auth.Write.ConditionCheck;
        var names = check.ExpressionAttributeNames;
        var values = check.ExpressionAttributeValues;
        names.Add("#catalogRevision", "catalogRevision");
        names.Add("#catalogHead", "catalogHead");
        names.Add("#catalogGeneration", "catalogGeneration");
        names.Add("#catalogEpoch", "catalogEpoch");
        names.Add("#catalogPending", "catalogPending");
        values.Add(":nextCatalogRevision", NumberValue(replacement.Revision));
        values.Add(":nextCatalogHead", StringValue(JsonSerializer.Serialize(replacement)));
        values.Add(":nextCatalogGeneration", NumberValue(replacement.AppliedGeneration));
        values.Add(":nextCatalogEpoch", NumberValue(replacement.AppliedEpoch));
        string condition = check.ConditionExpression;
        if (expected is null)
        {
            condition += " AND attribute_not_exists(#catalogRevision)";
        }
        else
        {
            condition += " AND #catalogRevision = :oldCatalogRevision";
            values.Add(":oldCatalogRevision", NumberValue(expected.Revision));
        }
        string update = "SET #catalogRevision = :nextCatalogRevision, #catalogHead = :nextCatalogHead, " +
            "#catalogGeneration = :nextCatalogGeneration, #catalogEpoch = :nextCatalogEpoch";
        if (replacement.Pending is not null)
        {
            update += ", #catalogPending = :pending";
            values.Add(":pending", StringValue(replacement.Pending.PlanDigest));
        }
        else
        {
            update += " REMOVE #catalogPending";
        }
        return new TransactionAction(auth.Key, new TransactWriteItem
        {
            Update = new Update
            {
                Key = check.Key,
                TableName = check.TableName,
                ConditionExpression = condition,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
                UpdateExpression = update,
            },
        });
    }

    private static void RequireAppliedCatalog(TransactionAction leaseCondition, CatalogProfile expected)
    {
        var condition = leaseCondition.Write.ConditionCheck;
        condition.ConditionExpression += " AND #catalogGeneration = :appliedGeneration AND #catalogEpoch = :epoch" +
            " AND attribute_not_exists(#catalogPending)";
        condition.ExpressionAttributeNames["#catalogGeneration"] = "catalogGeneration";
        condition.ExpressionAttributeNames["#catalogEpoch"] = "catalogEpoch";
        condition.ExpressionAttributeNames["#catalogPending"] = "catalogPending";
        condition.ExpressionAttributeValues[":appliedGeneration"] = NumberValue(expected.AppliedGeneration);
    }

    private static void RequireCatalogSessionOpen(TransactionAction leaseCondition, CatalogProfile? expected)
    {
        if (expected is not null)
        {
            RequireAppliedCatalog(leaseCondition, expected);
            return;
        }

        var check = leaseCondition.Write.ConditionCheck;
        check.ConditionExpression += " AND attribute_not_exists(catalogRevision)";
    }

    private static void ProtectCatalogRecordWrite(TransactionAction action)
    {
        if (action.Key.Kind == LifecycleItemKind.Claim &&
            action.Key.PartitionKey.StartsWith("CLAIM#" + CatalogClaimPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("This claim requires the catalog ownership protocol.", nameof(action));
        }
        if (action.Key.Kind == LifecycleItemKind.Profile)
        {
            action.Write.Put.ConditionExpression += " AND attribute_not_exists(catalogPublished) AND attribute_not_exists(catalogCandidate)";
        }
    }

    private TransactionAction CatalogProfileCondition(CatalogProfile expected) =>
        RecordRevisionCondition(new LifecycleRecordCondition(LifecycleKey.Profile(expected.Profile), expected.Revision));

    private TransactionAction CatalogAllowsSessionOpenCondition(LifecycleProfile profile, CatalogProfile? expected)
    {
        if (expected is { State: CatalogProfileState.Active or CatalogProfileState.Draining })
        {
            return CatalogProfileCondition(expected);
        }

        var key = LifecycleKey.Profile(profile);
        return new TransactionAction(key, new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = _tableName,
                Key = Key(key),
                ConditionExpression = expected is null
                    ? "attribute_not_exists(catalogPublished) AND attribute_not_exists(catalogCandidate)"
                    : "attribute_not_exists(PK)",
            },
        });
    }

    private static bool ValidProfileData(CatalogProfileData value)
    {
        var binding = value.Binding;
        return binding is not null && !string.IsNullOrWhiteSpace(binding.ScopeIdentity) &&
            !string.IsNullOrWhiteSpace(binding.RunnerGroup) && !string.IsNullOrWhiteSpace(binding.ScaleSet) &&
            !string.IsNullOrWhiteSpace(binding.ProviderDefinition) && !string.IsNullOrWhiteSpace(binding.BootstrapRoleArn) &&
            !string.IsNullOrWhiteSpace(binding.BootstrapProtocol) && Enum.IsDefined(binding.ProvisionerType) &&
            !binding.Labels.IsDefaultOrEmpty && binding.Labels.All(label => !string.IsNullOrWhiteSpace(label)) &&
            binding.Labels.SequenceEqual(binding.Labels.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) &&
            binding.RunnerGroupId is not < 0 && binding.ScaleSetId is not <= 0 &&
            (binding.RunnerGroupId is null || binding.ScaleSetId is not null) && (value.State switch
            {
                CatalogProfileState.Active or CatalogProfileState.Draining => value.ClosureHeldAt is null && value.RetiredAt is null,
                CatalogProfileState.ClosureHeld => value.ClosureHeldAt is not null && value.RetiredAt is null,
                CatalogProfileState.Tombstoned => value.ClosureHeldAt is { } held && value.RetiredAt >= held,
                _ => false,
            });
    }

    private static CatalogBinding ConfigurationBinding(RuntimeConfiguration configuration, RunnerProfile profile)
    {
        string identity = profile.Provisioner switch
        {
            Ec2ProvisionerConfiguration ec2 => ec2.LaunchTemplateId,
            FargateProvisionerConfiguration fargate => fargate.TaskDefinitionArn,
            _ => throw new ArgumentException("Unsupported profile provisioner.", nameof(profile)),
        };
        return new CatalogBinding(GitHubRoutingIdentity.Scope(configuration.GitHub[profile.GitHub.Connection].ScopeUrl),
            GitHubRoutingIdentity.Name(profile.GitHub.RunnerGroup), GitHubRoutingIdentity.Name(profile.GitHub.ScaleSet),
            profile.GitHub.Labels.Select(GitHubRoutingIdentity.Name).Order(StringComparer.Ordinal).ToImmutableArray(),
            profile.Provisioner.Type, identity, profile.Provisioner.BootstrapRoleArn, profile.Provisioner.BootstrapProtocol,
            null, profile.GitHub.ScaleSetId);
    }

    private static bool SameBinding(CatalogBinding left, CatalogBinding right) =>
        left with { Labels = right.Labels } == right && left.Labels.SequenceEqual(right.Labels);

    private static bool SameProfile(CatalogProfile? left, CatalogProfile? right) => left is not null && right is not null &&
        left with { Binding = right.Binding } == right && SameBinding(left.Binding, right.Binding);

    private static T CatalogDeserialize<T>(string value) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, JsonSerializerOptions.Strict) ?? throw InvalidRecord();
        }
        catch (JsonException)
        {
            throw InvalidRecord();
        }
    }

    private static bool IsCatalogDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(character => char.IsAsciiHexDigitLower(character));

    private static string CatalogDigest<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private sealed record CatalogProfileData(
        CatalogBinding Binding, CatalogProfileState State, DateTimeOffset? ClosureHeldAt = null, DateTimeOffset? RetiredAt = null)
    {
        internal static CatalogProfileData From(CatalogProfile profile) =>
            new(profile.Binding, profile.State, profile.ClosureHeldAt, profile.RetiredAt);
    }

    private sealed record StagedCatalogProfile(long Generation, string Digest, CatalogProfileData Data);

    private sealed record CatalogRow(
        LifecycleProfile Profile, long Revision, Guid TransitionId, string Payload,
        CatalogProfileData? Published, StagedCatalogProfile? Candidate)
    {
        internal CatalogProfile? Effective(CatalogRolloutHead head)
        {
            CatalogProfileData? data = Candidate is { } candidate && candidate.Generation == head.AppliedGeneration &&
                candidate.Digest == head.AppliedDigest ? candidate.Data : Published;
            return data is null ? null : new CatalogProfile(Profile, data.Binding, data.State,
                head.AppliedGeneration, Revision, TransitionId, data.ClosureHeldAt, data.RetiredAt);
        }
    }
}
