using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.Telemetry.Tests;

internal enum ReceiverReply
{
    Accepted = 0,
    Partial = 1,
    Rejected = 2,
    StalledBody = 3,
    Oversized = 4,
    GrpcStatusWithoutTrailer = 5,
}

internal sealed record OtlpCapture(string Path, string MediaType, string Protocol, string Authorization, byte[] Body)
{
    internal byte[] Payload()
    {
        if (!MediaType.StartsWith("application/grpc", StringComparison.Ordinal))
        {
            return Body;
        }
        if (Body.Length < 5 || Body[0] != 0 || BinaryPrimitives.ReadUInt32BigEndian(Body.AsSpan(1, 4)) != Body.Length - 5)
        {
            throw new InvalidDataException("Expected one uncompressed unary gRPC frame.");
        }
        return Body[5..];
    }
}

internal sealed class OtlpLoopback : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<OtlpCapture> _requests = new();
    private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stallEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseStalledBody = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private OtlpLoopback(WebApplication app) => _app = app;

    internal string Url => _app.Urls.Single();
    internal IReadOnlyList<OtlpCapture> Requests => _requests.ToArray();
    internal Task FirstRequest => _received.Task;
    internal Task StalledBodyStarted => _stallStarted.Task;
    internal Task StalledBodyEnded => _stallEnded.Task;

    internal void ReleaseStalledBody() => _releaseStalledBody.TrySetResult();

    internal static async Task<OtlpLoopback> StartAsync(bool grpc, Func<OtlpCapture, ReceiverReply>? respond, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = grpc ? HttpProtocols.Http2 : HttpProtocols.Http1));
        var app = builder.Build();
        var fixture = new OtlpLoopback(app);
        app.Run(context => fixture.ReceiveAsync(context, grpc, respond));
        await app.StartAsync(cancellationToken);
        return fixture;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private async Task ReceiveAsync(HttpContext context, bool grpc, Func<OtlpCapture, ReceiverReply>? respond)
    {
        var cancellationToken = context.RequestAborted;
        await using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, cancellationToken);
        var request = new OtlpCapture(context.Request.Path.Value!, context.Request.ContentType!, context.Request.Protocol,
            context.Request.Headers.Authorization.ToString(), body.ToArray());
        _requests.Enqueue(request);
        _received.TrySetResult();

        var reply = respond?.Invoke(request) ?? ReceiverReply.Accepted;
        if (!grpc && reply == ReceiverReply.Rejected)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("private-collector-rejection", cancellationToken);
            return;
        }

        context.Response.ContentType = grpc ? "application/grpc" : "application/x-protobuf";
        if (reply == ReceiverReply.StalledBody)
        {
            await context.Response.StartAsync(cancellationToken);
            _stallStarted.TrySetResult();
            try
            {
                await _releaseStalledBody.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                _stallEnded.TrySetResult();
            }
            return;
        }

        byte[] response = reply switch
        {
            ReceiverReply.Partial => [10, 2, 8, 1],
            ReceiverReply.Oversized => new byte[65_537],
            _ => [],
        };
        if (grpc)
        {
            byte[] frame = new byte[response.Length + 5];
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)response.Length);
            response.CopyTo(frame.AsSpan(5));
            if (reply == ReceiverReply.GrpcStatusWithoutTrailer)
            {
                context.Response.Headers.Append("grpc-status", "0");
            }
            else
            {
                context.Response.AppendTrailer("grpc-status", reply == ReceiverReply.Rejected ? "7" : "0");
            }
            await context.Response.Body.WriteAsync(frame, cancellationToken);
            return;
        }
        await context.Response.Body.WriteAsync(response, cancellationToken);
    }
}
