using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

internal sealed class JitRunnerConfiguration : IDisposable
{
    private static readonly string[] Names = [".runner", ".credentials", ".credentials_rsaparams"];
    private readonly byte[][] _members = new byte[Names.Length][];

    internal long RunnerId { get; private set; }

    internal static JitRunnerConfiguration Decode(ReadOnlySpan<byte> encodedJit, string expectedName)
    {
        var decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(encodedJit.Length)];
        var configuration = new JitRunnerConfiguration();
        bool accepted = false;
        try
        {
            if (Base64.DecodeFromUtf8(encodedJit, decoded, out int consumed, out int written) != OperationStatus.Done ||
                consumed != encodedJit.Length)
            {
                throw InvalidJit();
            }

            var reader = new Utf8JsonReader(decoded.AsSpan(0, written));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw InvalidJit();
            }
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                int member = MemberIndex(ref reader);
                if (member < 0 || configuration._members[member] is not null || !reader.Read() ||
                    reader.TokenType != JsonTokenType.String || !reader.TryGetBytesFromBase64(out var payload) || payload.Length == 0)
                {
                    throw InvalidJit();
                }
                configuration._members[member] = payload;
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() ||
                configuration._members.Any(static value => value is null))
            {
                throw InvalidJit();
            }

            configuration.RunnerId = ValidateRunnerSettings(configuration._members[0], expectedName);
            accepted = true;
            return configuration;
        }
        catch (JsonException)
        {
            throw InvalidJit();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
            if (!accepted)
            {
                configuration.Dispose();
            }
        }
    }

    [SupportedOSPlatform("linux")]
    internal void Materialize(ILinuxOneJobBoundary boundary)
    {
        for (int index = 0; index < Names.Length; index++)
        {
            boundary.WriteConfigFile(Names[index], _members[index]);
        }
    }

    public void Dispose()
    {
        foreach (byte[]? member in _members)
        {
            if (member is not null)
            {
                CryptographicOperations.ZeroMemory(member);
            }
        }
    }

    private static int MemberIndex(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
        {
            return -1;
        }
        if (reader.ValueTextEquals(".runner"u8))
        {
            return 0;
        }
        if (reader.ValueTextEquals(".credentials"u8))
        {
            return 1;
        }
        return reader.ValueTextEquals(".credentials_rsaparams"u8) ? 2 : -1;
    }

    private static long ValidateRunnerSettings(byte[] settings, string expectedName)
    {
        using var document = JsonDocument.Parse(settings);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw InvalidJit();
        }
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in document.RootElement.EnumerateObject())
        {
            if (!values.TryAdd(value.Name, value.Value))
            {
                throw InvalidJit();
            }
        }
        if (!values.TryGetValue("AgentId", out var id) || id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out long runnerId) || runnerId <= 0 ||
            !values.TryGetValue("AgentName", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != expectedName ||
            !values.TryGetValue("Ephemeral", out var ephemeral) || ephemeral.ValueKind != JsonValueKind.True ||
            !values.TryGetValue("WorkFolder", out var work) || work.ValueKind != JsonValueKind.String || work.GetString() != "_work")
        {
            throw InvalidJit();
        }
        return runnerId;
    }

    private static InvalidDataException InvalidJit() => new("The ephemeral runner configuration is invalid.");
}
