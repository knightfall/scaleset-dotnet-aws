using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

internal static class ConfigurationFixtures
{
    public const string Ec2Latest = "ec2-only-latest";
    public const string Ec2Pinned = "ec2-only-pinned";
    public const string Fargate = "fargate-only";
    public const string Mixed = "mixed-ec2-fargate";

    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "ConfigFixtures");

    public static byte[] Runtime(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDirectory, "runtime", name + ".json"));

    public static byte[] EditRuntime(string name, params ConfigurationEdit[] edits)
    {
        var json = JsonNode.Parse(Runtime(name))!.AsObject();
        foreach (var edit in edits)
        {
            ApplyEdit(json, edit);
        }

        return JsonSerializer.SerializeToUtf8Bytes(json);
    }

    public static ConfigurationRejection[] Rejections() => JsonSerializer.Deserialize<ConfigurationRejection[]>(
        File.ReadAllBytes(Path.Combine(FixtureDirectory, "rejections.json")),
        JsonSerializerOptions.Web) ?? throw new InvalidDataException("The shared configuration rejection manifest is missing.");

    public static string Sha256(ReadOnlySpan<byte> json) => Convert.ToHexStringLower(SHA256.HashData(json));

    private static void ApplyEdit(JsonObject json, ConfigurationEdit edit)
    {
        var path = edit.Path.Split('/');
        if (path.Length < 2 || path[0].Length != 0)
        {
            throw new InvalidDataException($"An edit requires a JSON Pointer path: {edit.Path}.");
        }

        JsonNode current = json;
        for (var index = 1; index < path.Length - 1; index++)
        {
            var segment = DecodeSegment(path[index]);
            var child = current is JsonArray array
                ? array[int.Parse(segment, NumberStyles.None, CultureInfo.InvariantCulture)]
                : current[segment];
            current = child ?? throw new InvalidDataException($"The edit path does not exist: {edit.Path}.");
        }

        var property = DecodeSegment(path[^1]);
        var parent = current.AsObject();
        if (edit.Value.ValueKind == JsonValueKind.Undefined)
        {
            if (!parent.Remove(property))
            {
                throw new InvalidDataException($"The property to remove does not exist: {edit.Path}.");
            }

            return;
        }

        parent[property] = JsonNode.Parse(edit.Value.GetRawText());
    }

    private static string DecodeSegment(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}

internal sealed record ConfigurationRejection(
    string Name,
    string Fault,
    string Code,
    string? Base,
    string? Yaml,
    ConfigurationEdit[]? Edits,
    string? ErrorPath,
    string[]? Redacted);

internal sealed record ConfigurationEdit(string Path, JsonElement Value)
{
    public static ConfigurationEdit Set<T>(string path, T value) => new(path, JsonSerializer.SerializeToElement(value));
}
