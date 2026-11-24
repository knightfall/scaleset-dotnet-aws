using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed record AuditedAwsRequest(
    string Service, string Operation, string Method, string Target, string? VersionId, string Body, bool Forwarded);

/// <summary>Records CLI traffic before forwarding permitted reads to the isolated MiniStack instance.</summary>
internal sealed class LoopbackAwsAuditProxy(WebApplication app, Uri upstream) : IAsyncDisposable
{
    private const int BufferLimit = 1024 * 1024;
    private static readonly HashSet<(string Service, string Operation)> AwsReads =
    [
        ("sts", "GetCallerIdentity"),
        ("ec2", "DescribeInstances"), ("ec2", "DescribeLaunchTemplateVersions"),
        ("ec2", "DescribeInstanceTypes"), ("ec2", "DescribeImages"),
        ("ec2", "DescribeSubnets"), ("ec2", "DescribeSecurityGroups"), ("ec2", "DescribeRouteTables"),
        ("iam", "GetInstanceProfile"), ("iam", "GetRole"),
        ("ecs", "DescribeClusters"), ("ecs", "ListTasks"),
        ("ecs", "DescribeTasks"), ("ecs", "DescribeTaskDefinition"), ("ecs", "ListAccountSettings"),
    ];
    private readonly ConcurrentQueue<AuditedAwsRequest> _requests = new();
    private readonly HttpClient _relay = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = BufferLimit,
    };

    internal Uri Endpoint { get; private set; } = null!;
    internal AuditedAwsRequest[] Requests => _requests.ToArray();

    internal static async Task<LoopbackAwsAuditProxy> StartAsync(Uri upstream, CancellationToken token)
    {
        if (upstream is not { Scheme: "http", Host: "127.0.0.1", IsDefaultPort: false, PathAndQuery: "/",
            UserInfo.Length: 0, Fragment.Length: 0 })
        {
            throw new ArgumentException("The audit proxy requires the isolated MiniStack endpoint.", nameof(upstream));
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = BufferLimit;
            options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        var proxy = new LoopbackAwsAuditProxy(app, upstream);
        app.Run(proxy.ReceiveAsync);
        try
        {
            await app.StartAsync(token);
            proxy.Endpoint = new Uri(Assert.Single(app.Urls));
            return proxy;
        }
        catch
        {
            await proxy.DisposeAsync();
            throw;
        }
    }

    private async Task ReceiveAsync(HttpContext context)
    {
        var request = context.Request;
        var token = request.HttpContext.RequestAborted;
        string target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? request.Path.ToUriComponent();
        if (request.Method == "CONNECT" || request.Host.Value != Endpoint.Authority ||
            !target.StartsWith('/'))
        {
            _requests.Enqueue(new AuditedAwsRequest("egress", request.Method, request.Method, target, null, "", Forwarded: false));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        string path = request.Path.ToUriComponent() + request.QueryString.ToUriComponent();
        if (!Uri.TryCreate(upstream, path, out var uri) || uri.Authority != upstream.Authority || uri.Scheme != upstream.Scheme)
        {
            _requests.Enqueue(new AuditedAwsRequest("egress", request.Method, request.Method, target, null, "", Forwarded: false));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, token);
        byte[] bytes = buffer.ToArray();
        string body = Encoding.UTF8.GetString(bytes);
        string service = ReadSigningService(request.Headers.Authorization.ToString());
        string? version = request.Query.TryGetValue("versionId", out var values) && values.Count == 1 ? values[0] : null;
        string operation = ReadOperation(request, service, body);
        bool permitted = service == "s3"
            ? request.Method == "GET" && operation == "GetObject" && !string.IsNullOrWhiteSpace(version)
            : request.Method == "POST" && AwsReads.Contains((service, operation));
        _requests.Enqueue(new AuditedAwsRequest(service, operation, request.Method, target, version, body, permitted));
        if (!permitted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var outgoing = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        if (bytes.Length > 0)
        {
            outgoing.Content = new ByteArrayContent(bytes);
            _ = outgoing.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
        }
        foreach (var (header, value) in request.Headers)
        {
            if (header.StartsWith("X-Amz-", StringComparison.OrdinalIgnoreCase) || header == "Authorization")
            {
                _ = outgoing.Headers.TryAddWithoutValidation(header, value.ToArray());
            }
        }

        using var response = await _relay.SendAsync(outgoing, token);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
        foreach (var (header, value) in response.Headers)
        {
            if (header.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers[header] = new StringValues(value.ToArray());
            }
        }
        byte[] content = await response.Content.ReadAsByteArrayAsync(token);
        context.Response.ContentLength = content.Length;
        await context.Response.Body.WriteAsync(content, token);
    }

    private static string ReadSigningService(string authorization)
    {
        const string credential = "Credential=";
        int start = authorization.IndexOf(credential, StringComparison.Ordinal);
        if (start < 0)
        {
            return "unsigned";
        }

        var scope = authorization[(start + credential.Length)..].Split(',', 2)[0].Split('/');
        return scope is [_, _, _, var service, "aws4_request"] ? service : "unknown";
    }

    private static string ReadOperation(HttpRequest request, string service, string body)
    {
        string target = request.Headers["X-Amz-Target"].ToString();
        if (target.Length > 0)
        {
            return target[(target.LastIndexOf('.') + 1)..];
        }
        if (service == "s3" && request.Method == "GET")
        {
            return "GetObject";
        }

        var fields = QueryHelpers.ParseQuery(body);
        return fields.TryGetValue("Action", out var action) && action.Count == 1 ? action.ToString() : request.Method;
    }

    public async ValueTask DisposeAsync()
    {
        await app.DisposeAsync();
        _relay.Dispose();
    }
}
