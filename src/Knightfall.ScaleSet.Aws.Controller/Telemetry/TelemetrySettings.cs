using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Telemetry;

namespace Knightfall.ScaleSet.Aws.Controller.Telemetry;

public sealed class TelemetrySettings
{
    private const string Exporter = "OTEL_EXPORTER_OTLP_";
    private static readonly string InstanceId = Guid.NewGuid().ToString("N");
    private static readonly FrozenSet<string> ReservedHeaders = new[]
    {
        "accept-encoding", "connection", "content-encoding", "content-length", "content-type",
        "expect", "host", "keep-alive", "proxy-authorization", "te", "trailer", "transfer-encoding", "upgrade",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private TelemetrySettings(bool disabled, IReadOnlyDictionary<string, object> attributes, FrozenDictionary<string, string> profiles,
        TelemetryEndpoint? traces, TelemetryEndpoint? metrics, TelemetryEndpoint? logs, double traceRatio, int metricIntervalMilliseconds)
    {
        IsDisabled = disabled;
        ResourceAttributes = attributes;
        Profiles = profiles;
        Traces = traces;
        Metrics = metrics;
        Logs = logs;
        TraceRatio = traceRatio;
        MetricIntervalMilliseconds = metricIntervalMilliseconds;
    }

    public bool IsDisabled { get; }
    public IReadOnlyDictionary<string, object> ResourceAttributes { get; }
    internal FrozenDictionary<string, string> Profiles { get; }
    internal TelemetryEndpoint? Traces { get; }
    internal TelemetryEndpoint? Metrics { get; }
    internal TelemetryEndpoint? Logs { get; }
    internal double TraceRatio { get; }
    internal int MetricIntervalMilliseconds { get; }

    public static TelemetrySettings Load(string? endpointOverride = null, RuntimeConfiguration? runtime = null,
        Func<string, string?>? readEnvironment = null)
    {
        var read = readEnvironment ?? Environment.GetEnvironmentVariable;
        bool disabled = ReadBoolean(read("OTEL_SDK_DISABLED"), "OTEL_SDK_DISABLED");
        var attributes = Resource(read);
        var commonEndpoint = ReadUri(read(Exporter + "ENDPOINT"), Exporter + "ENDPOINT");
        var commonProtocol = ReadProtocol(read(Exporter + "PROTOCOL"), Exporter + "PROTOCOL") ?? TelemetryProtocol.Grpc;
        int commonTimeout = ReadMilliseconds(read(Exporter + "TIMEOUT"), 3_000, minimum: 1, maximum: 5_000, Exporter + "TIMEOUT");
        var commonHeaders = ReadHeaders(read(Exporter + "HEADERS"), Exporter + "HEADERS") ?? FrozenDictionary<string, string>.Empty;
        var common = new CommonSettings(commonEndpoint, commonProtocol, commonTimeout, commonHeaders);
        var overridden = ReadUri(endpointOverride, "--otlp-endpoint");
        var traces = ReadEndpoint(read, TelemetrySignal.Traces, common, overridden);
        var metrics = ReadEndpoint(read, TelemetrySignal.Metrics, common, overridden);
        var logs = ReadEndpoint(read, TelemetrySignal.Logs, common, overridden);
        double ratio = ReadTraceRatio(read);
        int interval = ReadMilliseconds(read("OTEL_METRIC_EXPORT_INTERVAL"), 30_000, minimum: 1_000,
            maximum: 3_600_000, "OTEL_METRIC_EXPORT_INTERVAL");
        var settings = new TelemetrySettings(disabled, attributes, FrozenDictionary<string, string>.Empty,
            disabled ? null : traces, disabled ? null : metrics,
            disabled ? null : logs, ratio, interval);
        return runtime is null ? settings : settings.WithRuntimeConfiguration(runtime);
    }

    internal TelemetrySettings WithRuntimeConfiguration(RuntimeConfiguration runtime)
    {
        var attributes = new Dictionary<string, object>(ResourceAttributes, StringComparer.Ordinal)
        {
            ["scaleset.deployment.name"] = runtime.EnvironmentName,
            ["cloud.provider"] = "aws",
            ["cloud.account.id"] = runtime.Aws.Account,
            ["cloud.region"] = runtime.Aws.Region,
        };
        return new TelemetrySettings(IsDisabled, attributes.ToFrozenDictionary(StringComparer.Ordinal), ReadProfiles(runtime),
            Traces, Metrics, Logs, TraceRatio, MetricIntervalMilliseconds);
    }

    private static TelemetryEndpoint? ReadEndpoint(Func<string, string?> read, TelemetrySignal signal, CommonSettings common, Uri? endpointOverride)
    {
        string prefix = Exporter + signal.ToString().ToUpperInvariant() + "_";
        string signalKey = prefix + "ENDPOINT";
        string? configured = read(signalKey);
        if (endpointOverride is not null && configured is not null)
        {
            throw Invalid(signalKey);
        }

        var signalEndpoint = ReadUri(configured, signalKey);
        var protocol = ReadProtocol(read(prefix + "PROTOCOL"), prefix + "PROTOCOL") ?? common.Protocol;
        string timeoutKey = prefix + "TIMEOUT";
        int timeout = ReadMilliseconds(read(timeoutKey), common.Timeout, minimum: 1, maximum: 5_000, timeoutKey);
        var headers = ReadHeaders(read(prefix + "HEADERS"), prefix + "HEADERS") ?? common.Headers;
        var endpoint = endpointOverride ?? signalEndpoint ?? common.Endpoint;
        if (endpoint is null)
        {
            return null;
        }

        var wireEndpoint = protocol switch
        {
            TelemetryProtocol.Grpc => AppendPath(endpoint, signal.GrpcMethod()),
            _ when signalEndpoint is not null => endpoint,
            _ => AppendPath(endpoint, signal.HttpPath()),
        };
        return new TelemetryEndpoint(signal, protocol, wireEndpoint, TimeSpan.FromMilliseconds(timeout), headers);
    }

    private static FrozenDictionary<string, object> Resource(Func<string, string?> read)
    {
        const string nameKey = "OTEL_SERVICE_NAME";
        const string resourceKey = "OTEL_RESOURCE_ATTRIBUTES";
        string service = read(nameKey) ?? "scaleset-aws-controller";
        if (!IsIdentity(service, 128))
        {
            throw Invalid(nameKey);
        }

        var overrides = ReadPairs(read(resourceKey), resourceKey);
        string tier = "development";
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (key != "deployment.environment.name" || !IsIdentity(value, 64))
                {
                    throw Invalid(resourceKey);
                }
                tier = value;
            }
        }

        var attributes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["service.name"] = service,
            ["service.namespace"] = "knightfall.scaleset.aws",
            ["service.version"] = typeof(TelemetrySettings).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            ["service.instance.id"] = InstanceId,
            ["deployment.environment.name"] = tier,
        };
        return attributes.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenDictionary<string, string> ReadProfiles(RuntimeConfiguration runtime)
    {
        var profiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var profile in runtime.Profiles)
        {
            if (TelemetryProfile.From(profile) is { } identity)
            {
                profiles.Add(identity.Id, identity.Provider);
            }
        }
        return profiles.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static double ReadTraceRatio(Func<string, string?> read)
    {
        const string samplerKey = "OTEL_TRACES_SAMPLER";
        const string ratioKey = "OTEL_TRACES_SAMPLER_ARG";
        string? sampler = read(samplerKey);
        string? ratio = read(ratioKey);
        if (sampler == "parentbased_always_on")
        {
            if (ratio is not null)
            {
                throw Invalid(ratioKey);
            }
            return 1;
        }
        if (sampler is not (null or "parentbased_traceidratio"))
        {
            throw Invalid(samplerKey);
        }
        if (ratio is null)
        {
            return 0.1;
        }
        if (!double.TryParse(ratio, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double result) ||
            !(result is >= 0 and <= 1))
        {
            throw Invalid(ratioKey);
        }
        return result;
    }

    private static bool ReadBoolean(string? value, string setting)
    {
        if (value is null)
        {
            return false;
        }
        return bool.TryParse(value, out bool parsed) ? parsed : throw Invalid(setting);
    }

    private static TelemetryProtocol? ReadProtocol(string? value, string setting) => value switch
    {
        null => null,
        "grpc" => TelemetryProtocol.Grpc,
        "http/protobuf" => TelemetryProtocol.HttpProtobuf,
        _ => throw Invalid(setting),
    };

    private static int ReadMilliseconds(string? value, int fallback, int minimum, int maximum, string setting)
    {
        if (value is null)
        {
            return fallback;
        }
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) &&
            parsed >= minimum && parsed <= maximum ? parsed : throw Invalid(setting);
    }

    private static Uri? ReadUri(string? value, string setting)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Length > 2_048 || value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
            endpoint.Host.Length == 0 || endpoint.Port <= 0 || endpoint.UserInfo.Length != 0 ||
            value.Contains('?') || value.Contains('#') ||
            !endpoint.IsWellFormedOriginalString())
        {
            throw Invalid(setting);
        }
        return endpoint;
    }

    private static Uri AppendPath(Uri endpoint, string path) =>
        new(endpoint.AbsoluteUri.TrimEnd('/') + "/" + path, UriKind.Absolute);

    private static FrozenDictionary<string, string>? ReadHeaders(string? value, string setting)
    {
        var headers = ReadPairs(value, setting);
        if (headers is null)
        {
            return null;
        }
        foreach (var (key, headerValue) in headers)
        {
            if (key.Length is 0 or > 64 || key.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
                key.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase) || key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase) ||
                ReservedHeaders.Contains(key) || headerValue.Length > 2_048 || headerValue.Any(static character => character is < ' ' or > '~'))
            {
                throw Invalid(setting);
            }
        }
        return headers.ToFrozenDictionary(static pair => pair.Key.ToLowerInvariant(), static pair => pair.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, string>? ReadPairs(string? value, string setting)
    {
        if (value is null)
        {
            return null;
        }
        if (value.Length > 8_192)
        {
            throw Invalid(setting);
        }
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value.Length == 0)
        {
            return pairs;
        }
        foreach (string pair in value.Split(','))
        {
            int delimiter = pair.IndexOf('=');
            if (pairs.Count == 16 || delimiter <= 0 ||
                !pairs.TryAdd(Unescape(pair[..delimiter].Trim(), setting), Unescape(pair[(delimiter + 1)..].Trim(), setting)))
            {
                throw Invalid(setting);
            }
        }
        return pairs;
    }

    private static string Unescape(string value, string setting)
    {
        for (int index = value.IndexOf('%'); index >= 0; index = value.IndexOf('%', index + 3))
        {
            if (index + 2 >= value.Length || !char.IsAsciiHexDigit(value[index + 1]) || !char.IsAsciiHexDigit(value[index + 2]))
            {
                throw Invalid(setting);
            }
        }
        return Uri.UnescapeDataString(value);
    }

    private static bool IsIdentity(string value, int maximum) => value.Length is > 0 && value.Length <= maximum &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static TelemetrySettingsException Invalid(string setting) => new(setting);

    private sealed record CommonSettings(Uri? Endpoint, TelemetryProtocol Protocol, int Timeout, IReadOnlyDictionary<string, string> Headers);
}

public sealed class TelemetrySettingsException(string setting) : Exception($"Invalid telemetry setting {setting}.")
{
    public string Setting { get; } = setting;
}
