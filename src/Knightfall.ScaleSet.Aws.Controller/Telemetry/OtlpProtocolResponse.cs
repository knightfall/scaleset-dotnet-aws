using System.Buffers.Binary;
using System.Net;
using Google.Protobuf;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

internal enum ExportReceipt
{
    Accepted = 0,
    AcceptedWithWarning = 1,
    PartiallyRejected = 2,
    Rejected = 3,
    InvalidResponse = 4,
    Timeout = 5,
    Unavailable = 6,
}

internal static class OtlpProtocolResponse
{
    /// <summary>Classifies the shared response format for all three OTLP signals.</summary>
    internal static ExportReceipt Classify(HttpResponseMessage response, byte[] body, TelemetryProtocol protocol)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return ExportReceipt.Rejected;
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (protocol == TelemetryProtocol.HttpProtobuf)
        {
            return mediaType == "application/x-protobuf" ? Parse(body) : ExportReceipt.InvalidResponse;
        }

        if (mediaType is not ("application/grpc" or "application/grpc+proto") ||
            !TryReadGrpcStatus(response, out string? status))
        {
            return ExportReceipt.InvalidResponse;
        }

        if (status != "0")
        {
            return ExportReceipt.Rejected;
        }
        return GrpcPayload(body) is { } payload ? Parse(payload) : ExportReceipt.InvalidResponse;
    }

    internal static byte[]? GrpcPayload(byte[] body)
    {
        const int headerSize = 5;
        if (body.Length < headerSize || body[0] != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1, 4)) != body.Length - headerSize)
        {
            return null;
        }
        return body[headerSize..];
    }

    private static bool TryReadGrpcStatus(HttpResponseMessage response, out string? status)
    {
        status = null;
        if (!response.TrailingHeaders.TryGetValues("grpc-status", out var values) &&
            !response.Headers.TryGetValues("grpc-status", out values))
        {
            return false;
        }
        using var statuses = values.GetEnumerator();
        if (!statuses.MoveNext())
        {
            return false;
        }
        status = statuses.Current;
        return !statuses.MoveNext() && status is { Length: > 0 and <= 2 } && status.All(char.IsAsciiDigit);
    }

    private static ExportReceipt Parse(byte[] body)
    {
        try
        {
            using var input = new CodedInputStream(body);
            ExportReceipt receipt = ExportReceipt.Accepted;
            bool hasPartial = false;
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (tag != 10)
                {
                    input.SkipLastField();
                    continue;
                }
                if (hasPartial)
                {
                    return ExportReceipt.InvalidResponse;
                }
                hasPartial = true;
                using var partial = input.ReadBytes().CreateCodedInput();
                long rejected = 0;
                bool hasMessage = false;
                uint field;
                while ((field = partial.ReadTag()) != 0)
                {
                    switch (field)
                    {
                        case 8:
                            rejected = partial.ReadInt64();
                            break;
                        case 18:
                            hasMessage = !partial.ReadBytes().IsEmpty;
                            break;
                        default:
                            partial.SkipLastField();
                            break;
                    }
                }
                receipt = rejected switch
                {
                    < 0 => ExportReceipt.InvalidResponse,
                    > 0 => ExportReceipt.PartiallyRejected,
                    _ when hasMessage => ExportReceipt.AcceptedWithWarning,
                    _ => ExportReceipt.Accepted,
                };
            }
            return receipt;
        }
        catch (InvalidProtocolBufferException)
        {
            return ExportReceipt.InvalidResponse;
        }
    }
}
