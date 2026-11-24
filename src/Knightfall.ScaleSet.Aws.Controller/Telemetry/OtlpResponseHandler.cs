using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using OpenTelemetry;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal sealed class OtlpResponseHandler(
    TelemetryEndpoint endpoint,
    Action<TelemetrySignal, ExportReceipt> report,
    IMeterFactory meterFactory,
    Action<int>? releaseLogRecords,
    CancellationToken shutdown) : DelegatingHandler(new SocketsHttpHandler
    {
        ActivityHeadersPropagator = null,
    MeterFactory = meterFactory,
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        MaxResponseDrainSize = 0,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
{
    internal const int MaximumResponseBytes = 64 * 1_024;

    /// <inheritdoc/>
    /// <remarks>The pinned exporters use this for HTTP/1.1. Async socket reads keep the response body within the deadline.</remarks>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var suppress = SuppressInstrumentationScope.Begin();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cancellationToken);
        deadline.CancelAfter(endpoint.Timeout);
        int logRecords = 0;
        HttpResponseMessage? response = null;
        try
        {
            if (releaseLogRecords is not null && request.Content is not null)
            {
                // Its producer is the bounded, sanitized Serilog batch. Use the
                // exact serialized record count to release admission slots.
                byte[] outgoing = await request.Content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false);
                logRecords = CountLogs(endpoint.Protocol == TelemetryProtocol.Grpc ? OtlpProtocolResponse.GrpcPayload(outgoing) : outgoing);
            }

            deadline.Token.ThrowIfCancellationRequested();
            request.RequestUri = endpoint.Uri;
            request.Headers.Remove("grpc-accept-encoding");
            foreach (var (name, value) in endpoint.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
            byte[] body = response.StatusCode == HttpStatusCode.OK
                ? await ReadBodyAsync(response.Content, deadline.Token).ConfigureAwait(false) : [];
            var receipt = OtlpProtocolResponse.Classify(response, body, endpoint.Protocol);
            report(endpoint.Signal, receipt);
            if (releaseLogRecords is not null && receipt is not (ExportReceipt.Accepted or ExportReceipt.AcceptedWithWarning or
                ExportReceipt.PartiallyRejected))
            {
                // This handler owns best-effort log delivery and receipts. A
                // terminal failure drops this batch without Serilog retrying it
                // or purging other queued batches after repeated rejections.
                response.Dispose();
                return EndLogBatch();
            }

            var content = new ByteArrayContent(body);
            foreach (var header in response.Content.Headers)
            {
                if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            response.Content.Dispose();
            response.Content = content;
            response.Headers.Remove("grpc-message");
            response.TrailingHeaders.Remove("grpc-message");
            return response;
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            report(endpoint.Signal, ExportReceipt.Timeout);
            if (releaseLogRecords is not null)
            {
                return EndLogBatch();
            }
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or FormatException)
        {
            response?.Dispose();
            report(endpoint.Signal, error is InvalidDataException ? ExportReceipt.InvalidResponse : ExportReceipt.Unavailable);
            if (releaseLogRecords is not null)
            {
                return EndLogBatch();
            }
            throw;
        }
        finally
        {
            releaseLogRecords?.Invoke(logRecords);
        }
    }

    private HttpResponseMessage EndLogBatch()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (endpoint.Protocol == TelemetryProtocol.Grpc)
        {
            response.Version = HttpVersion.Version20;
            response.Content = new ByteArrayContent(new byte[5]);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.Add("grpc-status", "0");
        }
        else
        {
            response.Content = new ByteArrayContent([]);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        }
        return response;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("The OTLP response exceeded its size limit.");
        }
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var captured = new MemoryStream();
        byte[] buffer = new byte[4_096];
        int size;
        while ((size = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (captured.Length + size > MaximumResponseBytes)
            {
                throw new InvalidDataException("The OTLP response exceeded its size limit.");
            }
            captured.Write(buffer, 0, size);
        }
        return captured.ToArray();
    }

    private static int CountLogs(byte[]? payload)
    {
        if (payload is null)
        {
            throw new InvalidDataException("The bounded OTLP log batch could not be read.");
        }
        using var request = new CodedInputStream(payload);
        int count = 0;
        while (request.ReadTag() is uint resourceTag && resourceTag != 0)
        {
            if (resourceTag != 10)
            {
                request.SkipLastField();
                continue;
            }
            using var resource = request.ReadBytes().CreateCodedInput();
            while (resource.ReadTag() is uint scopeTag && scopeTag != 0)
            {
                if (scopeTag != 18)
                {
                    resource.SkipLastField();
                    continue;
                }
                using var scope = resource.ReadBytes().CreateCodedInput();
                while (scope.ReadTag() is uint recordTag && recordTag != 0)
                {
                    if (recordTag == 18)
                    {
                        count++;
                    }
                    scope.SkipLastField();
                }
            }
        }
        return count;
    }
}
