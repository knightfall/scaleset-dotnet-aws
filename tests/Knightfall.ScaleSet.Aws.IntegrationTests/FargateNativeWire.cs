using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Controller.Configuration;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed record FargateNativeRequest(string Operation, string Body);

internal enum FargateSubnetEvidence
{
    Native = 0,
    Missing = 1,
    Zero = 2,
}

/// <summary>Relays the pinned SDK to native MiniStack with narrow Dockerless ECS wire corrections.</summary>
internal sealed class FargateNativeWire(Uri endpoint) : DelegatingHandler(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
})
{
    private const string Ecs = "AmazonEC2ContainerServiceV20141113.";
    private const string DynamoDb = "DynamoDB_20120810.";
    private readonly ConcurrentQueue<FargateNativeRequest> _requests = new();
    private readonly ConcurrentDictionary<string, NativeTask> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Cluster, string Token), RunReceipt> _receipts = [];
    private readonly SemaphoreSlim _launches = new(1);
    private string? _hiddenStartedBy;
    private int _dropResponses;
    private int _nativeLaunches;

    internal FargateNativeRequest[] Requests => _requests.ToArray();
    internal int NativeLaunches => Volatile.Read(ref _nativeLaunches);
    internal bool OmitLongArnSetting { get; set; }
    internal FargateSubnetEvidence SubnetEvidence { get; set; }

    internal T Configure<T>() where T : ClientConfig, new()
    {
        var options = ControllerAwsClientConfiguration.Create<T>(FargateNativeFixture.Region, endpoint);
        options.HttpClientFactory = new RelayHttpFactory(this);
        return options;
    }

    internal void DropNextRunResponse() => Interlocked.Increment(ref _dropResponses);

    internal void HideNextStartedBy(string startedBy) => Volatile.Write(ref _hiddenStartedBy, startedBy);

    internal string NativeArn(string taskArn) => _tasks[taskArn].Arn;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { } used || used.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority))
        {
            throw new InvalidOperationException("The Fargate relay only permits the pinned loopback endpoint.");
        }

        string? target = request.Headers.TryGetValues("X-Amz-Target", out var values) ? values.Single() : null;
        if (target?.StartsWith(Ecs, StringComparison.Ordinal) == true)
        {
            string operation = target[Ecs.Length..];
            string body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _requests.Enqueue(new FargateNativeRequest(operation, body));
            return await RelayEcsAsync(request, operation, body, cancellationToken).ConfigureAwait(false);
        }

        if (target?.StartsWith(DynamoDb, StringComparison.Ordinal) == true)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _requests.Enqueue(new FargateNativeRequest(target[DynamoDb.Length..], body));
        }
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (SubnetEvidence == FargateSubnetEvidence.Native || response.StatusCode != HttpStatusCode.OK || request.Content is null ||
            !(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
                .Split('&').Contains("Action=DescribeSubnets", StringComparer.Ordinal))
        {
            return response;
        }
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        foreach (var element in xml.Descendants().Where(element => element.Name.LocalName == "availableIpAddressCount").ToArray())
        {
            if (SubnetEvidence == FargateSubnetEvidence.Zero)
            {
                element.Value = "0";
            }
            else
            {
                element.Remove();
            }
        }
        return Replace(response, xml.ToString(SaveOptions.DisableFormatting));
    }

    private async Task<HttpResponseMessage> RelayEcsAsync(
        HttpRequestMessage request, string operation, string body, CancellationToken cancellationToken)
    {
        var input = JsonNode.Parse(body)!.AsObject();
        if (operation == "RunTask")
        {
            return await RunAsync(request, input, body, cancellationToken).ConfigureAwait(false);
        }
        if (operation == "DescribeTasks")
        {
            var tasks = input["tasks"]!.AsArray();
            for (int index = 0; index < tasks.Count; index++)
            {
                string arn = tasks[index]!.GetValue<string>();
                if (_tasks.TryGetValue(arn, out var mapped))
                {
                    tasks[index] = mapped.Arn;
                }
            }
            RewriteRequest(request, input);
        }
        if (operation == "StopTask" && input["task"]?.GetValue<string>() is { } stopped && _tasks.TryGetValue(stopped, out var task))
        {
            input["task"] = task.Arn;
            RewriteRequest(request, input);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            operation is not ("DescribeTasks" or "DescribeTaskDefinition" or "ListTasks" or "StopTask" or "ListAccountSettings"))
        {
            return response;
        }

        var output = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
        switch (operation)
        {
            case "DescribeTaskDefinition":
                // MiniStack emits an empty string for omitted PID mode. ECS represents the default by omission.
                if (output["taskDefinition"] is JsonObject definition && definition["pidMode"]?.GetValue<string>() == string.Empty)
                {
                    definition.Remove("pidMode");
                }
                break;
            case "DescribeTasks":
                foreach (var item in output["tasks"]!.AsArray())
                {
                    NormalizeTask(item!);
                }
                break;
            case "ListTasks":
                var arns = output["taskArns"]!.AsArray();
                for (int index = 0; index < arns.Count; index++)
                {
                    arns[index] = NormalizeArn(arns[index]!.GetValue<string>());
                }
                var hidden = Volatile.Read(ref _hiddenStartedBy);
                if (hidden is not null && input["startedBy"]?.GetValue<string>() == hidden &&
                    ReferenceEquals(Interlocked.CompareExchange(ref _hiddenStartedBy, null, hidden), hidden))
                {
                    arns.Clear();
                }
                break;
            case "StopTask":
                NormalizeTask(output["task"]!);
                break;
            case "ListAccountSettings" when OmitLongArnSetting:
                output["settings"]!.AsArray().Clear();
                break;
        }
        return Replace(response, output.ToJsonString());
    }

    private async Task<HttpResponseMessage> RunAsync(
        HttpRequestMessage request, JsonObject input, string body, CancellationToken cancellationToken)
    {
        var identity = (input["cluster"]!.GetValue<string>(), input["clientToken"]!.GetValue<string>());
        await _launches.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_receipts.TryGetValue(identity, out var receipt))
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return response;
                }
                using (response)
                {
                    var output = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
                    var created = output["tasks"]!.AsArray();
                    if (created.Count != 1 || output["failures"]!.AsArray().Count != 0)
                    {
                        throw new InvalidDataException("Native MiniStack must return exactly one Fargate task.");
                    }
                    var item = created[0]!;
                    string arn = item["taskArn"]!.GetValue<string>();
                    var subnet = input["networkConfiguration"]!["awsvpcConfiguration"]!["subnets"]![0]!.GetValue<string>();
                    string canonical = CanonicalArn(arn);
                    string ip = FormattableString.Invariant($"10.80.1.{_nativeLaunches + 10}");
                    if (!_tasks.TryAdd(canonical, new NativeTask(arn, subnet, ip)))
                    {
                        throw new InvalidDataException("Native MiniStack allocated a repeated task identity.");
                    }
                    Interlocked.Increment(ref _nativeLaunches);
                    NormalizeTask(item);
                    receipt = new RunReceipt(body, output.ToJsonString());
                    _receipts.Add(identity, receipt);
                }
            }
            else if (receipt.Request != body)
            {
                throw new InvalidDataException("A replay changed immutable ECS request bytes.");
            }

            if (Volatile.Read(ref _dropResponses) > 0)
            {
                Interlocked.Decrement(ref _dropResponses);
                throw new HttpRequestException("The fixture interrupted RunTask after native commit.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(receipt.Response, Encoding.UTF8, "application/x-amz-json-1.1"),
            };
        }
        finally
        {
            _launches.Release();
        }
    }

    private void NormalizeTask(JsonNode item)
    {
        string arn = item["taskArn"]!.GetValue<string>();
        string canonical = NormalizeArn(arn);
        item["taskArn"] = canonical;
        if (_tasks.TryGetValue(canonical, out var native) &&
            item["lastStatus"]?.GetValue<string>() == "RUNNING" &&
            item["attachments"] is JsonArray { Count: 0 } attachments)
        {
            attachments.Add(new JsonObject
            {
                ["type"] = "ElasticNetworkInterface",
                ["status"] = "ATTACHED",
                ["details"] = new JsonArray(
                    new JsonObject { ["name"] = "subnetId", ["value"] = native.Subnet },
                    new JsonObject { ["name"] = "networkInterfaceId", ["value"] = "eni-" + canonical[^17..] },
                    new JsonObject { ["name"] = "privateIPv4Address", ["value"] = native.Ip }),
            });
        }
    }

    private string NormalizeArn(string arn)
    {
        string canonical = CanonicalArn(arn);
        return _tasks.ContainsKey(canonical) ? canonical : arn;
    }

    private static string CanonicalArn(string arn)
    {
        int separator = arn.LastIndexOf('/');
        return Guid.TryParseExact(arn.AsSpan(separator + 1), "D", out var id)
            ? arn[..(separator + 1)] + id.ToString("N")
            : arn;
    }

    private static void RewriteRequest(HttpRequestMessage request, JsonObject body)
    {
        var original = request.Content!;
        string mediaType = original.Headers.ContentType?.MediaType ?? "application/x-amz-json-1.1";
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, mediaType);
        original.Dispose();
    }

    private static HttpResponseMessage Replace(HttpResponseMessage response, string body)
    {
        var original = response.Content;
        string mediaType = original.Headers.ContentType?.MediaType ?? "application/x-amz-json-1.1";
        response.Headers.Remove("x-amz-crc32");
        response.Content = new StringContent(body, Encoding.UTF8, mediaType);
        original.Dispose();
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _launches.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record NativeTask(string Arn, string Subnet, string Ip);
    private sealed record RunReceipt(string Request, string Response);

    private sealed class RelayHttpFactory(FargateNativeWire relay) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(relay, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}
