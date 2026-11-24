using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Amazon.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed record ImdsRequest(string Method, string Path, bool Authorized);

internal sealed class DirectEc2ImdsHost : IAsyncDisposable
{
    internal const string TokenPath = "/latest/api/token";
    internal const string DocumentPath = "/latest/dynamic/instance-identity/document";
    internal const string CredentialsPrefix = "/latest/meta-data/iam/security-credentials/";
    private const string TokenHeader = "X-aws-ec2-metadata-token";
    private const string TokenTtlHeader = "X-aws-ec2-metadata-token-ttl-seconds";
    private static readonly JsonSerializerOptions WireNames = new();
    private readonly WebApplication _app;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private readonly ConcurrentQueue<ImdsRequest> _requests = new();
    private long _issuedAt;

    private DirectEc2ImdsHost(WebApplication app) => _app = app;

    internal Uri Endpoint { get; private set; } = null!;
    internal ImdsRequest[] Requests => _requests.ToArray();

    internal static async Task<DirectEc2ImdsHost> StartAsync(string instanceId, string account, string region,
        string roleName, SessionAWSCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        ArgumentNullException.ThrowIfNull(credentials);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 0;
            options.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        var host = new DirectEc2ImdsHost(app);
        try
        {
            var roleCredentials = credentials.GetCredentials();
            app.MapPut(TokenPath, host.IssueToken);
            app.MapGet(DocumentPath, (HttpContext context) => host.Authorized(context) ? Results.Json(new
            {
                accountId = account,
                region,
                instanceId,
            }, WireNames) : Results.Unauthorized());
            app.MapGet(CredentialsPrefix + "{role}", (HttpContext context, string role) =>
            {
                if (role != roleName)
                {
                    host.Record(context, authorized: false);
                    return Results.NotFound();
                }
                return host.Authorized(context) ? Results.Json(new
                {
                    Code = "Success",
                    AccessKeyId = roleCredentials.AccessKey,
                    SecretAccessKey = roleCredentials.SecretKey,
                    Token = roleCredentials.Token,
                    Expiration = TimeProvider.System.GetUtcNow().AddMinutes(30),
                }, WireNames) : Results.Unauthorized();
            });
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            var server = app.Services.GetRequiredService<IServer>();
            host.Endpoint = new Uri(server.Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return host;
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IResult IssueToken(HttpContext context)
    {
        bool issued = context.Request.Headers[TokenTtlHeader] is { Count: 1 } ttl && ttl[0] == "60" &&
            !context.Request.Headers.ContainsKey("X-Forwarded-For");
        Record(context, issued);
        if (!issued)
        {
            return Results.BadRequest();
        }
        Volatile.Write(ref _issuedAt, TimeProvider.System.GetTimestamp());
        return Results.Text(_token);
    }

    private bool Authorized(HttpContext context)
    {
        long issued = Volatile.Read(ref _issuedAt);
        bool valid = issued != 0 && TimeProvider.System.GetElapsedTime(issued) < TimeSpan.FromSeconds(60) &&
            context.Request.Headers[TokenHeader] is { Count: 1 } token && token[0] == _token;
        Record(context, valid);
        return valid;
    }

    private void Record(HttpContext context, bool authorized) =>
        _requests.Enqueue(new ImdsRequest(context.Request.Method, context.Request.Path, authorized));

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
