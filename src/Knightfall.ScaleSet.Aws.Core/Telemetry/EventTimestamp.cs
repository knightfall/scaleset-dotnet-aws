using System.Text.Json;

namespace Knightfall.ScaleSet.Aws.Core.Telemetry;

/// <summary>Reads an optional offset-qualified EventBridge timestamp for diagnostics.</summary>
public static class EventTimestamp
{
    public static DateTimeOffset? Read(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("time", out var timestamp) ||
            timestamp.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        try
        {
            if (!timestamp.TryGetDateTimeOffset(out var parsed))
            {
                return null;
            }
            string text = timestamp.GetString()!;
            bool hasOffset = text.EndsWith('Z') || (text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':');
            return hasOffset ? parsed : null;
        }
        catch (InvalidOperationException)
        {
            // A JSON string with an unpaired escaped surrogate can fail during the timestamp conversion.
            // Invalid optional timing evidence must leave the authenticated readback path intact.
            return null;
        }
    }
}
