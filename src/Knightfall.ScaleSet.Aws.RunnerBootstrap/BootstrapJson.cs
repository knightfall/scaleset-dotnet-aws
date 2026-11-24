using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap;

internal static class BootstrapJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };
}
