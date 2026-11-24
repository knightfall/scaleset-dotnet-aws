using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed class ControllerHostAwsFixture : IAsyncDisposable
{
    internal const string Account = "111122223333";
    internal const string Region = "us-east-1";
    internal const string Cluster = "arn:aws:ecs:" + Region + ":" + Account + ":cluster/engineering-dev-controller";
    internal const string Task = "arn:aws:ecs:" + Region + ":" + Account + ":task/engineering-dev-controller/0123456789abcdef0123456789abcde1";
    internal const string PreviousTask = "arn:aws:ecs:" + Region + ":" + Account + ":task/engineering-dev-controller/0123456789abcdef0123456789abcde2";
    internal const string Secret = "arn:aws:secretsmanager:" + Region + ":" + Account + ":secret:controller-fixture-secret";
    private static readonly JsonSerializerOptions ExactWireNames = new();
    private static readonly string[] ForwardHeaders = ["X-Amz-Target", "X-Amz-Date", "X-Amz-Security-Token", "Authorization"];
    private static readonly string[] CurrentStage = ["AWSCURRENT"];

    private readonly WebApplication _app;
    private readonly HttpClient _ministack;
    private readonly Uri _ministackEndpoint;
    private readonly ConcurrentQueue<(string Service, string Operation)> _operations = new();
    private readonly ConcurrentQueue<string> _forwardFailures = new();
    private readonly TaskCompletionSource _observedRunning = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _previousStopped;

    private ControllerHostAwsFixture(WebApplication app, Uri ministack)
    {
        _app = app;
        _ministackEndpoint = ministack;
        _ministack = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
    }

    internal Uri Endpoint { get; private set; } = null!;

    internal Task ObservedRunning => _observedRunning.Task;

    internal (string Service, string Operation)[] Operations => _operations.ToArray();

    internal string[] ForwardFailures => _forwardFailures.ToArray();

    internal void StopPreviousTask() => Volatile.Write(ref _previousStopped, 1);

    internal static async Task<ControllerHostAwsFixture> StartAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable("SCALESET_MINISTACK_ENDPOINT"), UriKind.Absolute, out var ministack) ||
            ministack.Scheme != Uri.UriSchemeHttp || ministack.Host != "127.0.0.1" || ministack.IsDefaultPort || ministack.PathAndQuery != "/" ||
            ministack.UserInfo.Length != 0 || ministack.Fragment.Length != 0)
        {
            throw new InvalidOperationException("The host fixture requires loopback MiniStack.");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
            options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        var fixture = new ControllerHostAwsFixture(app, ministack);
        try
        {
            app.MapGet("/v4/fixture/task", fixture.ReadMetadata);
            app.MapPost("/", fixture.InvokeAsync);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var server = app.Services.GetRequiredService<IServer>();
            fixture.Endpoint = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IResult ReadMetadata()
    {
        _operations.Enqueue(("metadata", "task"));
        return Results.Json(new
        {
            Cluster,
            TaskARN = Task,
            LaunchType = "FARGATE",
            ClockDrift = new
            {
                ClockErrorBound = 0.446931,
                ReferenceTimestamp = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ClockSynchronizationStatus = "SYNCHRONIZED",
            },
        }, ExactWireNames);
    }

    private async Task InvokeAsync(HttpContext context)
    {
        var token = context.RequestAborted;
        string target = context.Request.Headers["X-Amz-Target"].ToString();
        if (target.StartsWith("DynamoDB_20120810.", StringComparison.Ordinal))
        {
            _operations.Enqueue(("dynamodb", target[(target.LastIndexOf('.') + 1)..]));
            await ForwardDynamoDbAsync(context, token).ConfigureAwait(false);
            return;
        }

        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync(token).ConfigureAwait(false);
        IResult result = target switch
        {
            "AmazonEC2ContainerServiceV20141113.DescribeTasks" => DescribePrevious(body),
            "secretsmanager.GetSecretValue" => ReadSecret(body),
            "" when body.Split('&').Contains("Action=GetCallerIdentity", StringComparer.Ordinal) => ReadCaller(),
            "" when context.Request.Headers.Authorization.ToString().Contains("/ec2/aws4_request", StringComparison.Ordinal) => ObserveEc2Read(body),
            _ => Unexpected(target),
        };
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    private IResult DescribePrevious(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.GetProperty("cluster").GetString() != Cluster || root.GetProperty("tasks").GetArrayLength() != 1 ||
            root.GetProperty("tasks")[0].GetString() != PreviousTask)
        {
            return Unexpected("DescribeTasks binding");
        }
        _operations.Enqueue(("ecs-controller", "DescribeTasks"));
        bool stopped = Volatile.Read(ref _previousStopped) == 1;
        if (!stopped)
        {
            _observedRunning.TrySetResult();
        }
        return Results.Json(new
        {
            tasks = new[]
            {
                new { taskArn = PreviousTask, clusterArn = Cluster, lastStatus = stopped ? "STOPPED" : "RUNNING", desiredStatus = stopped ? "STOPPED" : "RUNNING" },
            },
            failures = Array.Empty<object>(),
        });
    }

    private IResult ReadSecret(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.GetProperty("SecretId").GetString() != Secret || json.RootElement.GetProperty("VersionStage").GetString() != "AWSCURRENT")
        {
            return Unexpected("GetSecretValue binding");
        }
        _operations.Enqueue(("secretsmanager", "GetSecretValue"));
        return Results.Json(new
        {
            ARN = Secret,
            VersionId = "0123456789abcdef0123456789abcdef",
            VersionStages = CurrentStage,
            SecretString = JsonSerializer.Serialize(new { token = GitHubSimulator.FirstPat }),
        }, ExactWireNames);
    }

    private IResult ReadCaller()
    {
        _operations.Enqueue(("sts", "GetCallerIdentity"));
        return Results.Text($"""
            <GetCallerIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
              <GetCallerIdentityResult><Account>{Account}</Account><Arn>arn:aws:iam::{Account}:user/fixture</Arn><UserId>AIDAFIXTURE</UserId></GetCallerIdentityResult>
            </GetCallerIdentityResponse>
            """, "text/xml");
    }

    private IResult ObserveEc2Read(string body)
    {
        string? action = body.Split('&').SingleOrDefault(parameter => parameter.StartsWith("Action=", StringComparison.Ordinal));
        if (action?.StartsWith("Action=Describe", StringComparison.Ordinal) != true)
        {
            return Unexpected(action ?? "ec2 missing action");
        }
        _operations.Enqueue(("ec2", action["Action=".Length..]));
        return Results.Text("<Response><Errors><Error><Code>UnauthorizedOperation</Code><Message>Fixture withholds runner preflight.</Message></Error></Errors></Response>",
            "text/xml", statusCode: StatusCodes.Status403Forbidden);
    }

    private IResult Unexpected(string operation)
    {
        _operations.Enqueue(("unexpected", operation));
        return Results.BadRequest();
    }

    private async Task ForwardDynamoDbAsync(HttpContext context, CancellationToken cancellationToken)
    {
        await using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
        using var outgoing = new HttpRequestMessage(HttpMethod.Post, _ministackEndpoint)
        {
            Content = new ByteArrayContent(body.ToArray()),
        };
        outgoing.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType ?? "application/x-amz-json-1.0");
        foreach (string header in ForwardHeaders)
        {
            if (context.Request.Headers.TryGetValue(header, out var value))
            {
                _ = outgoing.Headers.TryAddWithoutValidation(header, value.ToString());
            }
        }
        using var result = await _ministack.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        context.Response.StatusCode = (int)result.StatusCode;
        context.Response.ContentType = result.Content.Headers.ContentType?.ToString();
        byte[] reply = await result.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccessStatusCode)
        {
            _forwardFailures.Enqueue(FormattableString.Invariant($"{result.StatusCode} {System.Text.Encoding.UTF8.GetString(reply)}"));
        }
        await context.Response.Body.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync().ConfigureAwait(false);
        _ministack.Dispose();
    }
}
