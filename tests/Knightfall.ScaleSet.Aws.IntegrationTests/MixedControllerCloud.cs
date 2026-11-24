using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Web;
using System.Xml.Linq;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed record MixedCloudCall(string Service, string Operation, string Request);

internal sealed record HeldEc2Launch(Guid AllocationId, string ClientToken, string ResourceArn, string TemplateId, long Version);

/// <summary>Keeps worker AWS calls on loopback and applies the provider-specific MiniStack corrections.</summary>
internal sealed class MixedControllerCloud : IAsyncDisposable
{
    internal const string Account = FargateNativeFixture.Account;
    internal const string Region = FargateNativeFixture.Region;
    private const string EcsTarget = "AmazonEC2ContainerServiceV20141113.";
    private static readonly string[] RelayHeaders = ["Authorization", "X-Amz-Date", "X-Amz-Target", "X-Amz-Security-Token"];
    private static readonly System.Text.Json.JsonSerializerOptions WireNames = new();
    internal static readonly BasicAWSCredentials Credentials = new(Account, "test-only-secret");

    private readonly WebApplication _app;
    private readonly HttpClient _native = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
    private readonly DirectEc2AwsWire _ec2Wire;
    private readonly FargateNativeWire _ecsWire;
    private readonly HttpClient _ec2;
    private readonly HttpClient _ecs;
    private readonly ConcurrentQueue<MixedCloudCall> _calls = new();
    private readonly ConcurrentDictionary<string, ControllerMetadata> _metadata = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<HeldEc2Launch> _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _recoveryRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string? _holdTemplate;
    private string? _recoveryToken;
    private int _degradeEc2;

    private MixedControllerCloud(WebApplication app, Uri nativeEndpoint)
    {
        _app = app;
        NativeEndpoint = nativeEndpoint;
        _ec2Wire = new DirectEc2AwsWire(nativeEndpoint, string.Empty);
        _ecsWire = new FargateNativeWire(nativeEndpoint);
        _ec2 = new HttpClient(_ec2Wire, disposeHandler: false);
        _ecs = new HttpClient(_ecsWire, disposeHandler: false);
    }

    internal Uri NativeEndpoint { get; }
    internal Uri Endpoint => new(_app.Urls.Single());
    internal MixedCloudCall[] Calls => _calls.ToArray();
    internal Task<HeldEc2Launch> HeldLaunch => _held.Task;
    internal Task RecoveryRead => _recoveryRead.Task;

    internal void DegradeEc2(bool enabled) => Volatile.Write(ref _degradeEc2, enabled ? 1 : 0);
    internal void HoldNextEc2Launch(string template) => Volatile.Write(ref _holdTemplate, template);
    internal void ReleaseRecoveryRead() => Volatile.Write(ref _recoveryToken, null);

    internal static async Task<MixedControllerCloud> StartAsync(CancellationToken token)
    {
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable("SCALESET_MINISTACK_ENDPOINT"), UriKind.Absolute, out var native) ||
            native.Scheme != Uri.UriSchemeHttp || native.Host != "127.0.0.1" || native.IsDefaultPort || native.PathAndQuery != "/" ||
            native.UserInfo.Length != 0 || native.Fragment.Length != 0)
        {
            throw new InvalidOperationException("The mixed worker fixture requires the pinned loopback MiniStack endpoint.");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
            server.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        var relay = new MixedControllerCloud(app, native);
        try
        {
            app.MapGet("/v4/{id}/task", relay.ReadMetadata);
            app.MapPost("/", relay.RelayAsync);
            await app.StartAsync(token);
            return relay;
        }
        catch
        {
            await relay.DisposeAsync();
            throw;
        }
    }

    internal async Task<string> StartControllerTaskAsync(string clusterArn, string definitionArn, string subnet, CancellationToken cancellationToken)
    {
        using var ecs = new AmazonECSClient(Credentials, _ecsWire.Configure<AmazonECSConfig>());
        string id = Guid.NewGuid().ToString("N");
        var result = await ecs.RunTaskAsync(new RunTaskRequest
        {
            Cluster = clusterArn,
            TaskDefinition = definitionArn,
            Count = 1,
            LaunchType = LaunchType.FARGATE,
            ClientToken = id,
            StartedBy = id,
            NetworkConfiguration = new NetworkConfiguration
            {
                AwsvpcConfiguration = new AwsVpcConfiguration { AssignPublicIp = AssignPublicIp.DISABLED, Subnets = [subnet] },
            },
        }, cancellationToken);
        Assert.Empty(result.Failures ?? []);
        var task = Assert.Single(result.Tasks);
        Assert.Equal("RUNNING", task.LastStatus);
        Assert.True(_metadata.TryAdd(ShortId(task.TaskArn), new ControllerMetadata(clusterArn, task.TaskArn)));
        return task.TaskArn;
    }

    internal Uri MetadataUri(string taskArn) => new(Endpoint, "v4/" + ShortId(taskArn));

    internal void SetClock(string taskArn, bool healthy) => _metadata[ShortId(taskArn)].SetClock(healthy);

    internal async Task StopControllerTaskAsync(string clusterArn, string taskArn, CancellationToken token)
    {
        using var ecs = new AmazonECSClient(Credentials, _ecsWire.Configure<AmazonECSConfig>());
        var response = await ecs.StopTaskAsync(new StopTaskRequest { Cluster = clusterArn, Task = taskArn }, token);
        Assert.Equal(taskArn, response.Task.TaskArn);
        Assert.Equal("STOPPED", response.Task.LastStatus);
    }

    private IResult ReadMetadata(string id)
    {
        if (!_metadata.TryGetValue(id, out var metadata))
        {
            return Results.NotFound();
        }
        bool healthy = metadata.HealthyClock;
        _calls.Enqueue(new MixedCloudCall("metadata", healthy ? "SYNCHRONIZED" : "NOT_SYNCHRONIZED", metadata.TaskArn));
        return Results.Json(new
        {
            Cluster = metadata.ClusterArn,
            TaskARN = metadata.TaskArn,
            LaunchType = "FARGATE",
            ClockDrift = new
            {
                ClockErrorBound = healthy ? 0.4 : 5000,
                ReferenceTimestamp = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ClockSynchronizationStatus = healthy ? "SYNCHRONIZED" : "NOT_SYNCHRONIZED",
            },
        }, WireNames);
    }

    private async Task RelayAsync(HttpContext context)
    {
        var token = context.RequestAborted;
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync(token);
        string target = context.Request.Headers["X-Amz-Target"].ToString();
        string authorization = context.Request.Headers.Authorization.ToString();
        string[] scope = authorization.Split('/');
        string service = scope.Length >= 5 ? scope[3] : string.Empty;
        NameValueCollection? query = target.Length == 0 ? HttpUtility.ParseQueryString(body) : null;
        string operation = target.Length == 0 ? query!["Action"] ?? "" : target[(target.LastIndexOf('.') + 1)..];
        _calls.Enqueue(new MixedCloudCall(service, operation, body));

        if (service == "ec2" && operation == "DescribeImages" && Volatile.Read(ref _degradeEc2) != 0)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("<Response><Errors><Error><Code>ServiceUnavailable</Code></Error></Errors></Response>", token);
            return;
        }

        string? recovery = Volatile.Read(ref _recoveryToken);
        if (service == "ec2" && operation == "DescribeInstances" && recovery is not null &&
            query!.AllKeys.Any(key => key is not null && query[key] == recovery))
        {
            _recoveryRead.TrySetResult();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("<Response><Errors><Error><Code>ServiceUnavailable</Code></Error></Errors></Response>", token);
            return;
        }

        using var outgoing = new HttpRequestMessage(HttpMethod.Post, NativeEndpoint) { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body)) };
        outgoing.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType ?? "application/octet-stream");
        foreach (string header in RelayHeaders)
        {
            if (context.Request.Headers.TryGetValue(header, out var values))
            {
                _ = outgoing.Headers.TryAddWithoutValidation(header, values.ToString());
            }
        }

        var http = service switch
        {
            "ec2" => _ec2,
            "ecs" when target.StartsWith(EcsTarget, StringComparison.Ordinal) => _ecs,
            _ => _native,
        };
        using var response = await http.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, token);
        string? hold = Volatile.Read(ref _holdTemplate);
        if (service == "ec2" && operation == "RunInstances" && response.IsSuccessStatusCode && hold is not null &&
            query!["LaunchTemplate.LaunchTemplateId"] == hold &&
            ReferenceEquals(Interlocked.CompareExchange(ref _holdTemplate, null, hold), hold))
        {
            string clientToken = query["ClientToken"]!;
            Volatile.Write(ref _recoveryToken, clientToken);
            _held.TrySetResult(await ReadLaunchReceiptAsync(query, response, clientToken, token));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
        await response.Content.CopyToAsync(context.Response.Body, token);
    }

    private static async Task<HeldEc2Launch> ReadLaunchReceiptAsync(NameValueCollection query, HttpResponseMessage response,
        string clientToken, CancellationToken token)
    {
        string allocationKey = query.AllKeys.Single(key => key?.StartsWith("TagSpecification.1.Tag.", StringComparison.Ordinal) == true &&
            key.EndsWith(".Key", StringComparison.Ordinal) && query[key] == RunnerOwnershipTags.Allocation)!;
        var allocation = Guid.ParseExact(query[allocationKey[..^3] + "Value"]!, "D");
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var ns = document.Root!.Name.Namespace;
        var instance = document.Descendants(ns + "instancesSet").Elements(ns + "item").Single();
        string resource = $"arn:aws:ec2:{Region}:{Account}:instance/{instance.Element(ns + "instanceId")!.Value}";
        return new HeldEc2Launch(allocation, clientToken, resource, query["LaunchTemplate.LaunchTemplateId"]!,
            long.Parse(query["LaunchTemplate.Version"]!, CultureInfo.InvariantCulture));
    }

    private static string ShortId(string taskArn) => taskArn[(taskArn.LastIndexOf('/') + 1)..];

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _native.Dispose();
        _ec2.Dispose();
        _ecs.Dispose();
        _ec2Wire.Dispose();
        _ecsWire.Dispose();
    }

    private sealed class ControllerMetadata(string clusterArn, string taskArn)
    {
        private int _healthy = 1;
        internal string ClusterArn { get; } = clusterArn;
        internal string TaskArn { get; } = taskArn;
        internal bool HealthyClock => Volatile.Read(ref _healthy) != 0;
        internal void SetClock(bool healthy) => Volatile.Write(ref _healthy, healthy ? 1 : 0);
    }
}
