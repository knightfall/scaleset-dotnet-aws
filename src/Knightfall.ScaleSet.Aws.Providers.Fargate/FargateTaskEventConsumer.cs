using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;

namespace Knightfall.ScaleSet.Aws.Providers.Fargate;

/// <summary>Consumes the Fargate EventBridge queue and reads exact task state from configured runner clusters.</summary>
public sealed class FargateTaskEventConsumer : IDisposable
{
    private const int MaximumStoredVersions = 4096;
    private readonly IAmazonSQS _sqs;
    private readonly FargateProfileInspector _inspector;
    private readonly RuntimeConfiguration _configuration;
    private readonly string _queueUrl;
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    private readonly Queue<string> _versionOrder = new();
    private readonly SemaphoreSlim _poll = new(1, 1);

    public FargateTaskEventConsumer(IAmazonSQS sqs, FargateProfileInspector inspector, RuntimeConfiguration configuration, string queueUrl)
    {
        ArgumentNullException.ThrowIfNull(sqs);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueUrl);
        if (configuration.Mode != ControllerMode.Active || !configuration.Profiles.Any(profile =>
            profile.State != RunnerProfileState.Disabled && profile.Provisioner is FargateProvisionerConfiguration))
        {
            throw new ArgumentException("The Fargate event consumer requires an active controller with enabled Fargate profiles.", nameof(configuration));
        }

        _sqs = sqs;
        _inspector = inspector;
        _configuration = configuration;
        _queueUrl = queueUrl;
    }

    public void Dispose() => _poll.Dispose();

    /// <summary>Polls one bounded batch. Owned task observations are acknowledged after the caller accepts them durably.</summary>
    public async Task PollAsync(Func<ExecutorBinding, ExecutorResourceObservation, CancellationToken, Task<bool>> apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apply);
        await _poll.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PollBatchAsync(apply, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _poll.Release();
        }
    }

    private async Task PollBatchAsync(Func<ExecutorBinding, ExecutorResourceObservation, CancellationToken, Task<bool>> apply,
        CancellationToken cancellationToken)
    {
        var result = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 10,
        }, cancellationToken).ConfigureAwait(false);

        var valid = new List<(Message Message, FargateTaskEvent Hint)>();
        foreach (var message in result.Messages ?? [])
        {
            if (string.IsNullOrEmpty(message?.ReceiptHandle))
            {
                continue;
            }
            if (FargateTaskEvent.Parse(message.Body, _configuration.Aws) is { } hint)
            {
                valid.Add((message, hint));
            }
            else
            {
                await AcknowledgeAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var group in valid.GroupBy(item => (item.Hint.TaskArn, item.Hint.ClusterArn, item.Hint.TaskDefinitionArn)))
        {
            var newest = group.MaxBy(item => item.Hint.Version).Hint;
            if (!_versions.TryGetValue(newest.TaskArn, out long version) || version < newest.Version)
            {
                var observation = await _inspector.ReadEventAsync(_configuration, newest, cancellationToken).ConfigureAwait(false);
                if (observation is null)
                {
                    continue;
                }
                if (observation is { Binding: { } binding, Resource: { } resource, Version: long readVersion })
                {
                    if (!await apply(binding, resource, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                    Remember(newest.TaskArn, readVersion);
                }
            }

            foreach (var (message, _) in group)
            {
                await AcknowledgeAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task AcknowledgeAsync(Message message, CancellationToken cancellationToken)
    {
        _ = await _sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = _queueUrl, ReceiptHandle = message.ReceiptHandle },
            cancellationToken).ConfigureAwait(false);
    }

    private void Remember(string taskArn, long version)
    {
        if (!_versions.ContainsKey(taskArn))
        {
            _versionOrder.Enqueue(taskArn);
        }
        _versions[taskArn] = version;
        if (_versionOrder.Count > MaximumStoredVersions)
        {
            _versions.Remove(_versionOrder.Dequeue());
        }
    }
}

internal sealed record FargateTaskEvent(string ClusterArn, string TaskArn, string TaskDefinitionArn, long Version)
{
    private const int MaximumEventLength = 256 * 1024;

    internal static FargateTaskEvent? Parse(string? body, AwsEnvironment aws)
    {
        if (string.IsNullOrEmpty(body) || body.Length > MaximumEventLength)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Value(root, "version") != "0" ||
                Value(root, "source") != "aws.ecs" || Value(root, "detail-type") != "ECS Task State Change" ||
                Value(root, "account") != aws.Account || Value(root, "region") != aws.Region ||
                !root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object ||
                !detail.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt64(out long parsed) || parsed < 0 ||
                Value(detail, "taskArn") is not { Length: > 0 } task || Value(detail, "clusterArn") is not { Length: > 0 } cluster ||
                Value(detail, "taskDefinitionArn") is not { Length: > 0 } definition ||
                !root.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array ||
                !resources.EnumerateArray().Any(arn => arn.ValueKind == JsonValueKind.String && arn.ValueEquals(task)))
            {
                return null;
            }

            return new FargateTaskEvent(cluster, task, definition, parsed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Value(JsonElement node, string field) =>
        node.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
