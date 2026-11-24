using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Knightfall.ScaleSet.Aws.Core.Configuration;

public static class RuntimeConfigurationLoader
{
    private const string ResourcePrefix = "Knightfall.ScaleSet.Aws.Core.Configuration.";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };
    private static readonly Lazy<JsonSchema> RuntimeSchema = new(BuildRuntimeSchema);
    private static readonly EvaluationOptions SchemaEvaluation = new() { OutputFormat = OutputFormat.List };

    public static RuntimeConfiguration Load(ReadOnlyMemory<byte> json, string? expectedSha256 = null)
    {
        // Hash and parse the same snapshot even if the caller's memory has a writable backing buffer.
        byte[] bytes = json.ToArray();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        VerifyDigest(sha256, expectedSha256);

        using var document = Parse(bytes);
        JsonElement root = document.RootElement;
        ValidateDecodedStrings(root);

        RuntimeConfigurationSemantics.RejectDeferredGitHubConfig(root);
        RequireRuntimeShape(root);
        RuntimeConfigurationSemantics.Validate(root);
        return RuntimeConfigurationTranslation.Translate(root, sha256);
    }

    private static void VerifyDigest(string sha256, string? expectedSha256)
    {
        if (expectedSha256 is null)
        {
            return;
        }

        if (expectedSha256.Length != SHA256.HashSizeInBytes * 2 || !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new RuntimeConfigurationException("digest", string.Empty, "Expected SHA-256 must contain exactly 64 hexadecimal digits.");
        }
        if (!string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeConfigurationException("digest", string.Empty, "The runtime configuration SHA-256 does not match the supplied digest.");
        }
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            return JsonDocument.Parse(bytes, StrictJson);
        }
        catch (DecoderFallbackException)
        {
            throw new RuntimeConfigurationException("json", string.Empty, "Runtime configuration must be valid UTF-8.");
        }
        catch (JsonException)
        {
            throw new RuntimeConfigurationException("json", string.Empty, "Runtime configuration must be a single valid JSON value with unique property names.");
        }
        catch (InvalidOperationException)
        {
            // Duplicate-name detection decodes property names as part of parsing.
            throw InvalidUnicodeString();
        }
    }

    private static void ValidateDecodedStrings(JsonElement root)
    {
        try
        {
            ReadStrings(root);
        }
        catch (InvalidOperationException)
        {
            throw InvalidUnicodeString();
        }
    }

    private static RuntimeConfigurationException InvalidUnicodeString() =>
        new("json", string.Empty, "Runtime configuration contains an invalid Unicode string.");

    private static void ReadStrings(JsonElement value)
    {
        // JsonDocument defers validation of escaped surrogate pairs until a string is read.
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    _ = property.Name;
                    ReadStrings(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ReadStrings(item);
                }
                break;
            case JsonValueKind.String:
                _ = value.GetString();
                break;
        }
    }

    private static void RequireRuntimeShape(JsonElement root)
    {
        var result = RuntimeSchema.Value.Evaluate(root, SchemaEvaluation);
        if (result.IsValid)
        {
            return;
        }

        string path = result.Details?
            .Where(detail => detail.Errors is { Count: > 0 })
            .Select(detail => detail.InstanceLocation.ToString())
            .MaxBy(location => location.Count(character => character == '/')) ?? string.Empty;
        throw new RuntimeConfigurationException("schema", path, "Runtime configuration violates the v1alpha1 schema.");
    }

    private static JsonSchema BuildRuntimeSchema()
    {
        var options = new BuildOptions
        {
            Dialect = Dialect.Draft202012,
            SchemaRegistry = new SchemaRegistry
            {
                Fetch = static (_, _) => throw new InvalidOperationException("A runtime schema reference was not embedded."),
            },
        };

        _ = JsonSchema.FromText(ReadSchema("controller-common-v1alpha1.json"), options, jsonOptions: StrictJson);
        return JsonSchema.FromText(ReadSchema("controller-runtime-v1alpha1.json"), options, jsonOptions: StrictJson);
    }

    private static string ReadSchema(string filename)
    {
        using var stream = typeof(RuntimeConfigurationLoader).Assembly.GetManifestResourceStream(ResourcePrefix + filename)
            ?? throw new InvalidOperationException("A runtime schema resource was not embedded.");
        using var reader = new StreamReader(stream, StrictUtf8);
        return reader.ReadToEnd();
    }
}
