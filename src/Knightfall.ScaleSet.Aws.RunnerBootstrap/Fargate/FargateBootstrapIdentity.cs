using System.Globalization;
using System.Text.Json;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;

internal sealed record FargateIdentityExpectation(
    string Account,
    string Region,
    string ClusterArn,
    string TaskFamily,
    string ContainerName,
    string TaskRoleArn,
    string TaskRoleId);

internal sealed record FargateTaskIdentity(string TaskArn, string TaskDefinitionArn, string AwsUserId)
{
    internal bool MatchesOffer(JitHandoffIdentity identity)
    {
        if (identity.ResourceArn != TaskArn || identity.AwsUserId != AwsUserId || identity.AllocationId == Guid.Empty)
        {
            return false;
        }

        return FargateExecutorIncarnation.Compute(TaskArn, TaskDefinitionArn, identity.AllocationId) == identity.Incarnation;
    }
}

internal static class FargateBootstrapIdentity
{
    private const string MetadataPrefix = "/v4/";
    private const string CredentialsPrefix = "/v2/credentials/";
    private const int MaximumMetadataBytes = 32 * 1024;
    private static readonly JsonDocumentOptions MetadataOptions = new() { MaxDepth = 12, AllowDuplicateProperties = false };

    internal static bool IsTaskCredentialsUri(string? value) =>
        value is not null && value.Length == CredentialsPrefix.Length + 36 && value.StartsWith(CredentialsPrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(value.AsSpan(CredentialsPrefix.Length), "D", out var id) && id != Guid.Empty;

    internal static async Task<FargateTaskIdentity> ReadAsync(string? metadataEndpoint, HttpClient metadata,
        IAmazonSecurityTokenService sts, FargateIdentityExpectation expected, TimeProvider clock, CancellationToken cancellationToken,
        bool allowLoopback = false)
    {
        var endpoint = ParseMetadataEndpoint(metadataEndpoint, allowLoopback);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        using var containerDocument = await ReadMetadataAsync(metadata, endpoint, token).ConfigureAwait(false);
        using var taskDocument = await ReadMetadataAsync(metadata, new Uri(endpoint.AbsoluteUri + "/task"), token).ConfigureAwait(false);
        var container = containerDocument.RootElement;
        var task = taskDocument.RootElement;

        string taskArn = RequireString(task, "TaskARN");
        int clusterSeparator = expected.ClusterArn.LastIndexOf('/');
        string clusterName = expected.ClusterArn[(clusterSeparator + 1)..];
        string taskPrefix = expected.ClusterArn.Replace(":cluster/", ":task/", StringComparison.Ordinal) + "/";
        string cluster = RequireString(task, "Cluster");
        string family = RequireString(task, "Family");
        string revisionText = RequireString(task, "Revision");
        if (!taskArn.StartsWith(taskPrefix, StringComparison.Ordinal) || (cluster != expected.ClusterArn && cluster != clusterName) ||
            family != expected.TaskFamily || !int.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out int revision) ||
            revision <= 0 || revisionText != revision.ToString(CultureInfo.InvariantCulture) ||
            task.TryGetProperty("ServiceName", out _) ||
            (task.TryGetProperty("LaunchType", out var type) && (type.ValueKind != JsonValueKind.String || !type.ValueEquals("FARGATE"))))
        {
            throw InvalidIdentity();
        }
        string taskId = taskArn[taskPrefix.Length..];
        if (taskId.Length != 32 || taskId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw InvalidIdentity();
        }

        ValidateContainer(container, task, expected.ContainerName, taskId);
        var caller = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), token).ConfigureAwait(false);
        string partition = expected.ClusterArn.Split(':', 3)[1];
        string roleName = expected.TaskRoleArn[(expected.TaskRoleArn.LastIndexOf('/') + 1)..];
        string awsUserId = expected.TaskRoleId + ":" + taskId;
        string assumedRole = $"arn:{partition}:sts::{expected.Account}:assumed-role/{roleName}/{taskId}";
        if (caller.Account != expected.Account || caller.UserId != awsUserId || caller.Arn != assumedRole ||
            !taskArn.StartsWith($"arn:{partition}:ecs:{expected.Region}:{expected.Account}:", StringComparison.Ordinal))
        {
            throw InvalidIdentity();
        }

        string definition = $"arn:{partition}:ecs:{expected.Region}:{expected.Account}:task-definition/{family}:{revisionText}";
        return new FargateTaskIdentity(taskArn, definition, awsUserId);
    }

    internal static Uri ParseMetadataEndpoint(string? value, bool allowLoopback = false)
    {
        // Only the ECS IPv4 authority and the explicit numeric loopback used by the integration fixture.
        if (value is not { Length: > 0 and <= 256 } || !value.StartsWith("http://", StringComparison.Ordinal) ||
            value.Contains('%') || value.Contains('\\') || value.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.AbsoluteUri != value ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            !((endpoint.Host == "169.254.170.2" && endpoint.IsDefaultPort) || (allowLoopback && endpoint.Host == "127.0.0.1")) ||
            !endpoint.AbsolutePath.StartsWith(MetadataPrefix, StringComparison.Ordinal))
        {
            throw InvalidIdentity();
        }
        string context = endpoint.AbsolutePath[MetadataPrefix.Length..];
        if (context.Length is < 1 or > 128 || context.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw InvalidIdentity();
        }
        return endpoint;
    }

    private static void ValidateContainer(JsonElement container, JsonElement task, string containerName, string taskId)
    {
        string runtimeId = RequireString(container, "DockerId");
        if (RequireString(container, "Name") != containerName || !runtimeId.StartsWith(taskId + "-", StringComparison.Ordinal) ||
            runtimeId.Length != taskId.Length + 11 || runtimeId[(taskId.Length + 1)..].Any(character => !char.IsAsciiDigit(character)) ||
            !task.TryGetProperty("Containers", out var members) || members.ValueKind != JsonValueKind.Array || members.GetArrayLength() != 1 ||
            RequireString(members[0], "Name") != containerName || RequireString(members[0], "DockerId") != runtimeId)
        {
            throw InvalidIdentity();
        }
    }

    private static async Task<JsonDocument> ReadMetadataAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        using var reply = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (reply.StatusCode != System.Net.HttpStatusCode.OK || reply.Content.Headers.ContentLength is > MaximumMetadataBytes)
        {
            throw InvalidIdentity();
        }

        await using var stream = await reply.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] bytes = new byte[MaximumMetadataBytes + 1];
        int length = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (length > MaximumMetadataBytes)
        {
            throw InvalidIdentity();
        }
        try
        {
            return JsonDocument.Parse(bytes.AsMemory(0, length), MetadataOptions);
        }
        catch (JsonException)
        {
            throw InvalidIdentity();
        }
    }

    private static string RequireString(JsonElement document, string key) =>
        document.ValueKind == JsonValueKind.Object && document.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 1024 } text ? text : throw InvalidIdentity();

    private static InvalidDataException InvalidIdentity() => new("The Fargate bootstrap identity could not be verified.");
}
