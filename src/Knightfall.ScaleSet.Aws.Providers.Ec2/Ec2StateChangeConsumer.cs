using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Providers.Ec2;

public sealed record Ec2StateChangeHint(string InstanceId, string ResourceArn);

/// <summary>Receives EC2 notification hints and acknowledges them after the caller processes authoritative readback.</summary>
public sealed class Ec2StateChangeConsumer
{
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly AwsEnvironment _aws;
    private readonly string _instanceArnPrefix;
    private readonly TimeProvider _clock;

    public Ec2StateChangeConsumer(RuntimeConfiguration configuration, IAmazonSQS sqs, string queueUrl)
        : this(configuration, sqs, queueUrl, TimeProvider.System)
    {
    }

    public Ec2StateChangeConsumer(RuntimeConfiguration configuration, IAmazonSQS sqs, string queueUrl, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sqs);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueUrl);
        ArgumentNullException.ThrowIfNull(clock);
        var ec2 = configuration.Profiles.FirstOrDefault(profile => profile.State != RunnerProfileState.Disabled &&
            profile.Provisioner is Ec2ProvisionerConfiguration);
        if (configuration.Mode != ControllerMode.Active || ec2 is null)
        {
            throw new ArgumentException("Only an active controller with an enabled EC2 profile may consume EC2 notifications.", nameof(configuration));
        }
        if (!Uri.TryCreate(queueUrl, UriKind.Absolute, out var queue) || queue.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("An absolute HTTP(S) queue URL is required.", nameof(queueUrl));
        }

        _sqs = sqs;
        _queueUrl = queueUrl;
        _aws = configuration.Aws;
        _clock = clock;
        string partition = ec2.Provisioner.BootstrapRoleArn.Split(':')[1];
        _instanceArnPrefix = $"arn:{partition}:ec2:{_aws.Region}:{_aws.Account}:instance/";
    }

    public async Task<int> ReceiveOnceAsync(
        Func<Ec2StateChangeHint, CancellationToken, Task<bool>> refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        ReceiveMessageResponse response;
        using (var receive = Ec2Diagnostics.Start("sqs_receive", null, cancellationToken))
        {
            response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = _queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 10,
            }, cancellationToken).ConfigureAwait(false);
            receive?.Complete("success", response.Messages is { Count: > 0 } ? "batch_received" : "queue_empty");
        }

        int acknowledged = 0;
        foreach (var message in response.Messages ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Ec2Diagnostics.Start("sqs_process", null, cancellationToken);
            if (message?.ReceiptHandle is not { Length: > 0 } receipt || ReadHint(message.Body) is not { } observation)
            {
                process?.Complete("rejected", "invalid_event");
                continue;
            }
            Ec2Diagnostics.RecordEventAge(observation.EventTime, _clock.GetUtcNow());
            if (!await refresh(observation.Hint, cancellationToken).ConfigureAwait(false))
            {
                process?.Complete("deferred", "readback_not_accepted");
                continue;
            }

            using (var delete = Ec2Diagnostics.Start("sqs_delete", null, cancellationToken))
            {
                await _sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = _queueUrl, ReceiptHandle = receipt },
                    cancellationToken).ConfigureAwait(false);
                delete?.Complete("success", "acknowledged");
            }
            process?.Complete("success", "readback_accepted");
            acknowledged++;
        }
        return acknowledged;
    }

    private (Ec2StateChangeHint Hint, DateTimeOffset? EventTime)? ReadHint(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (Property(root, "source") != "aws.ec2" || Property(root, "detail-type") != "EC2 Instance State-change Notification" ||
                Property(root, "account") != _aws.Account || Property(root, "region") != _aws.Region ||
                !root.TryGetProperty("detail", out var detail) || !root.TryGetProperty("resources", out var resources) ||
                resources.ValueKind != JsonValueKind.Array || resources.GetArrayLength() != 1 ||
                resources[0].ValueKind != JsonValueKind.String)
            {
                return null;
            }
            string? instanceId = Property(detail, "instance-id");
            if (!Ec2ProfileInspector.IsResourceId(instanceId, "i-") ||
                Property(detail, "state") is not ("pending" or "running" or "stopping" or "stopped" or "shutting-down" or "terminated"))
            {
                return null;
            }
            string arn = _instanceArnPrefix + instanceId;
            if (resources[0].GetString() != arn)
            {
                return null;
            }
            DateTimeOffset? eventTime = root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.String &&
                time.TryGetDateTimeOffset(out var parsed) ? parsed : null;
            return (new Ec2StateChangeHint(instanceId!, arn), eventTime);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
