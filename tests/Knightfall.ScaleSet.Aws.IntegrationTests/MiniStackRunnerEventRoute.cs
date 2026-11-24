using System.Net.Http.Json;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

/// <summary>Routes synthetic provider events through native MiniStack EventBridge and SQS.</summary>
internal sealed class MiniStackRunnerEventRoute : IAsyncDisposable
{
    private const string TargetId = "runner-queue";
    private const string DetailType = "Fixture runner state change";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions WireJson = new();
    private readonly HttpClient _events;
    private readonly string _name = "runner-state-" + Guid.NewGuid().ToString("N");
    private bool _ruleCreated;
    private bool _targetAdded;

    private MiniStackRunnerEventRoute(Uri endpoint)
    {
        Sqs = new AmazonSQSClient(new BasicAWSCredentials("000000000000", "test-only-secret"),
            ControllerAwsClientConfiguration.Create<AmazonSQSConfig>("us-east-1", endpoint));
        _events = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = endpoint,
            Timeout = Timeout,
            MaxResponseContentBufferSize = 64 * 1024,
        };
    }

    internal IAmazonSQS Sqs { get; }
    internal string QueueUrl { get; private set; } = string.Empty;
    internal string QueueArn { get; private set; } = string.Empty;

    internal static async Task<MiniStackRunnerEventRoute> CreateAsync(CancellationToken cancellationToken)
    {
        var raw = Environment.GetEnvironmentVariable("SCALESET_MINISTACK_ENDPOINT");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttp ||
            endpoint.Host != "127.0.0.1" || endpoint.IsDefaultPort || endpoint.PathAndQuery != "/" ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new InvalidOperationException("Use the pinned loopback MiniStack test launcher.");
        }

        var route = new MiniStackRunnerEventRoute(endpoint);
        try
        {
            await route.ConfigureAsync(cancellationToken).ConfigureAwait(false);
            return route;
        }
        catch
        {
            await route.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task PublishAsync(string exactAwsShapedEventJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactAwsShapedEventJson);
        using var payload = JsonDocument.Parse(exactAwsShapedEventJson);
        if (payload.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The provider envelope must be a JSON object.", nameof(exactAwsShapedEventJson));
        }
        // PutEvents reserves the AWS service source. The rule's InputPath delivers the enclosed provider envelope unchanged in meaning.
        using var result = await InvokeAsync("PutEvents", new
        {
            Entries = new[] { new { Source = Source(), DetailType, Detail = exactAwsShapedEventJson } },
        }, cancellationToken).ConfigureAwait(false);
        RequireComplete(result);
        var entries = result.RootElement.GetProperty("Entries");
        if (entries.GetArrayLength() != 1 || string.IsNullOrEmpty(entries[0].GetProperty("EventId").GetString()))
        {
            throw new InvalidOperationException("MiniStack did not accept the fixture event.");
        }
    }

    private async Task ConfigureAsync(CancellationToken cancellationToken)
    {
        var created = await Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = _name }, cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(created.QueueUrl, UriKind.Absolute, out var queue) || queue.Scheme != Uri.UriSchemeHttp ||
            queue.Authority != _events.BaseAddress!.Authority || queue.UserInfo.Length != 0 || queue.Query.Length != 0 ||
            queue.Fragment.Length != 0 || !queue.AbsolutePath.EndsWith("/" + _name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MiniStack returned a queue URL outside the loopback fixture.");
        }
        QueueUrl = created.QueueUrl;
        var attributes = await Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = QueueUrl,
            AttributeNames = ["QueueArn"],
        }, cancellationToken).ConfigureAwait(false);
        QueueArn = attributes.Attributes["QueueArn"];
        if (QueueArn != "arn:aws:sqs:us-east-1:000000000000:" + _name)
        {
            throw new InvalidOperationException("MiniStack returned a queue outside the fixture scope.");
        }
        string pattern = JsonSerializer.Serialize(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["source"] = [Source()],
            ["detail-type"] = [DetailType],
        });

        using var rule = await InvokeAsync("PutRule", new { Name = _name, EventPattern = pattern, State = "ENABLED" },
            cancellationToken).ConfigureAwait(false);
        _ruleCreated = true;
        using var target = await InvokeAsync("PutTargets", new
        {
            Rule = _name,
            Targets = new[] { new { Id = TargetId, Arn = QueueArn, InputPath = "$.detail" } },
        }, cancellationToken).ConfigureAwait(false);
        RequireComplete(target);
        _targetAdded = true;
    }

    private async Task<JsonDocument> InvokeAsync<T>(string operation, T input, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = JsonContent.Create(input, new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-amz-json-1.1"), WireJson),
        };
        request.Headers.Add("X-Amz-Target", "AWSEvents." + operation);
        using var response = await _events.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(bytes.Length == 0 ? "{}"u8.ToArray() : bytes);
    }

    private static void RequireComplete(JsonDocument response)
    {
        if (response.RootElement.GetProperty("FailedEntryCount").GetInt32() != 0)
        {
            throw new InvalidOperationException("MiniStack reported partial EventBridge failure.");
        }
    }

    private string Source() => "scaleset.fixture." + _name;

    public async ValueTask DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(Timeout);
        try
        {
            if (_targetAdded)
            {
                using var target = await InvokeAsync("RemoveTargets", new { Rule = _name, Ids = new[] { TargetId } }, cleanup.Token)
                    .ConfigureAwait(false);
                RequireComplete(target);
            }
            if (_ruleCreated)
            {
                using var rule = await InvokeAsync("DeleteRule", new { Name = _name }, cleanup.Token).ConfigureAwait(false);
            }
            if (QueueUrl.Length != 0)
            {
                await Sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = QueueUrl }, cleanup.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            Sqs.Dispose();
            _events.Dispose();
        }
    }
}

public sealed class MiniStackRunnerEventRouteTests
{
    [Fact]
    public async Task NativeEventBridgeRoutesOnlyMatchingProviderEnvelopesToEachQueue()
    {
        var token = TestContext.Current.CancellationToken;
        await using var ec2 = await MiniStackRunnerEventRoute.CreateAsync(token);
        await using var fargate = await MiniStackRunnerEventRoute.CreateAsync(token);
        const string instance = """
            {"source":"aws.ec2","detail-type":"EC2 Instance State-change Notification","detail":{"instance-id":"i-00000000000000013","state":"terminated"}}
            """;
        const string task = """
            {"source":"aws.ecs","detail-type":"ECS Task State Change","detail":{"lastStatus":"STOPPED","version":9}}
            """;
        await ec2.PublishAsync(instance, token);
        await fargate.PublishAsync(task, token);
        await ConfirmDeliveryAsync(ec2, instance, token);
        await ConfirmDeliveryAsync(fargate, task, token);
    }

    private static async Task ConfirmDeliveryAsync(MiniStackRunnerEventRoute route, string expected, CancellationToken cancellationToken)
    {
        var response = await route.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = route.QueueUrl,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 2,
        }, cancellationToken);
        var receipt = Assert.Single(response.Messages);
        using var wanted = JsonDocument.Parse(expected);
        using var observed = JsonDocument.Parse(receipt.Body);
        Assert.True(JsonElement.DeepEquals(wanted.RootElement, observed.RootElement));
        await route.Sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = route.QueueUrl, ReceiptHandle = receipt.ReceiptHandle },
            cancellationToken);
        var after = await route.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = route.QueueUrl }, cancellationToken);
        Assert.True(after.Messages is null or { Count: 0 });
    }
}
