using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Knightfall.ScaleSet.Aws.Controller.Commands;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Controller.Telemetry;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.GitHub;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Telemetry.Tests;

public sealed partial class TelemetryWireTests
{
    [Fact]
    public async Task ConsoleEmitsSanitizedJsonWithStructuredPropertiesAndCorrelation()
    {
        var token = TestContext.Current.CancellationToken;
        var configuration = RuntimeConfigurationLoader.Load(await MixedRuntimeAsync(token));
        var profile = Assert.IsType<TelemetryProfile>(TelemetryProfile.From(configuration.Profiles[0]));
        var settings = TelemetrySettings.Load(runtime: configuration, readEnvironment: static _ => null);
        await using var output = new StringWriter(CultureInfo.InvariantCulture);
        const string reference = "dc0fed00920ba342";
        string traceId;
        string spanId;
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            await using var pipeline = TelemetryPipeline.Start(settings, TextWriter.Null);
            var observer = pipeline.Logging.CreateLogger<CapturedEcsControllerTaskFenceObserver>();
            using (var trace = new Activity("fixture").SetIdFormat(ActivityIdFormat.W3C).Start())
            using (observer.BeginScope(new KeyValuePair<string, object?>[]
            {
                new("profile", profile.Id), new("provider", profile.Provider), new("reason", "predecessor_capture_verified"),
                new("evidence_reference", reference), new(Secret, new[] { Secret }),
            }))
            {
                traceId = trace.TraceId.ToHexString();
                spanId = trace.SpanId.ToHexString();
                WriteEvent(observer, "reconcile", "success", new IOException(Secret));
            }
            var other = pipeline.Logging.CreateLogger(Controller + "." + Secret);
            WriteUnknownEvent(other, Secret, new IOException(Secret));
            Assert.True(await pipeline.StopAsync().WaitAsync(token));
        }
        finally
        {
            Console.SetOut(original);
        }

        string rendered = output.ToString();
        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        var lines = rendered.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var known = JsonDocument.Parse(lines[0]);
        var log = known.RootElement;
        Assert.Equal(TimeSpan.Zero, log.GetProperty("Timestamp").GetDateTimeOffset().Offset);
        Assert.Equal("Warning", log.GetProperty("Level").GetString());
        Assert.Equal(TelemetryNames.EventTemplate, log.GetProperty("MessageTemplate").GetString());
        Assert.Contains("reconcile", log.GetProperty("RenderedMessage").GetString(), StringComparison.Ordinal);
        Assert.Equal(traceId, log.GetProperty("TraceId").GetString());
        Assert.Equal(spanId, log.GetProperty("SpanId").GetString());
        Assert.False(log.TryGetProperty("Exception", out _));
        var properties = log.GetProperty("Properties");
        Assert.Equal(7, properties.EnumerateObject().Count());
        Assert.Equal(Controller, properties.GetProperty("SourceContext").GetString());
        Assert.Equal("reconcile", properties.GetProperty("operation").GetString());
        Assert.Equal("success", properties.GetProperty("outcome").GetString());
        Assert.Equal("predecessor_capture_verified", properties.GetProperty("reason").GetString());
        Assert.Equal(profile.Id, properties.GetProperty("profile").GetString());
        Assert.Equal(profile.Provider, properties.GetProperty("provider").GetString());
        Assert.Equal(reference, properties.GetProperty("evidence_reference").GetString());

        using var unknown = JsonDocument.Parse(lines[1]);
        Assert.Equal("Controller diagnostic event", unknown.RootElement.GetProperty("RenderedMessage").GetString());
        Assert.False(unknown.RootElement.TryGetProperty("TraceId", out _));
        Assert.False(unknown.RootElement.TryGetProperty("SpanId", out _));
        Assert.False(unknown.RootElement.TryGetProperty("Exception", out _));
        Assert.Equal("SourceContext", Assert.Single(unknown.RootElement.GetProperty("Properties").EnumerateObject()).Name);
    }

    [Fact]
    public async Task RejectedCredentialCandidateExportsOnlyItsBoundedClassificationAndProfile()
    {
        var token = TestContext.Current.CancellationToken;
        var configuration = RuntimeConfigurationLoader.Load(await MixedRuntimeAsync(token));
        var configured = configuration.Profiles[0];
        var profile = Assert.IsType<TelemetryProfile>(TelemetryProfile.From(configured));
        var connection = configuration.GitHub[configured.GitHub.Connection];
        using var secrets = new RotatingSecrets();
        var loader = new GitHubSecretLoader(secrets);
        var clients = new GitHubScaleSetClientFactory();
        using var current = clients.Create(Assert.IsType<GitHubSecretVersion>(
            await loader.LoadAsync(configuration, configured.GitHub.Connection, token)));

        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, respond: null, token);
        var settings = TelemetrySettings.Load(runtime: configuration, readEnvironment: name => name switch
        {
            "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" => receiver.Url + "/v1/logs",
            "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL" => "http/protobuf",
            _ => null,
        });
        var original = Console.Out;
        await using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            await using var pipeline = TelemetryPipeline.Start(settings, TextWriter.Null);
            var logger = pipeline.Logging.CreateLogger<GitHubCredentialRotator>();
            var rotator = new GitHubCredentialRotator(loader, clients, logger);
            var routing = configured.GitHub;
            var binding = new GitHubScaleSetBinding(connection.ScopeUrl, routing.RunnerGroup, 1, routing.ScaleSet,
                routing.ScaleSetId ?? 1, routing.Labels);
            using (TelemetryProfileScope.Enter(profile))
            {
                var result = await rotator.CheckAsync(configuration, routing, binding, current, token);
                Assert.Equal(GitHubCredentialRotationStatus.CandidateRejected, result.Status);
                Assert.Null(result.Candidate);
                Assert.Equal(GitHubOperationError.CredentialUnavailable, result.Failure?.Error);
            }
            Assert.True(await pipeline.StopAsync().WaitAsync(token));
        }
        finally
        {
            Console.SetOut(original);
        }

        string rendered = output.ToString();
        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.SecretArn, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.ScopeUrl, rendered, StringComparison.Ordinal);
        using var console = JsonDocument.Parse(rendered);
        var fields = console.RootElement.GetProperty("Properties");
        var request = Assert.Single(receiver.Requests);
        var record = new OtlpMessage(request.Payload()).Message(1).Message(2).Message(2);
        var attributes = record.StringAttributes(6);
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(request.Payload()), StringComparison.Ordinal);
        Assert.DoesNotContain(connection.SecretArn, Encoding.UTF8.GetString(request.Payload()), StringComparison.Ordinal);
        Assert.DoesNotContain(connection.ScopeUrl, Encoding.UTF8.GetString(request.Payload()), StringComparison.Ordinal);
        foreach (var (key, value) in new KeyValuePair<string, string>[]
        {
            new("SourceContext", "Knightfall.ScaleSet.Aws.GitHub"), new("operation", "credential-rotate"),
            new("outcome", "rejected"), new("reason", "credential_unavailable"),
            new("profile", profile.Id), new("provider", profile.Provider),
        })
        {
            Assert.Equal(value, fields.GetProperty(key).GetString());
            Assert.Equal(value, attributes[key]);
        }
        Assert.Equal(6, fields.EnumerateObject().Count());
        Assert.Equal(6, attributes.Count);
    }

    [Fact]
    public async Task RunReportsFatalFailureToConsoleAndOtlpBeforeStoppingTelemetry()
    {
        var token = TestContext.Current.CancellationToken;
        string path = Path.Combine(AppContext.BaseDirectory, "mixed.json");
        string digest = Convert.ToHexStringLower(SHA256.HashData(await MixedRuntimeAsync(token)));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var receiver = await OtlpLoopback.StartAsync(grpc: true, respond: null, token);
        await using var output = new StringWriter(CultureInfo.InvariantCulture);
        await using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            Assert.Equal(1, await RunCommand.RunAsync(
                ["run", "--config", path, "--sha256", digest, "--generation", "7", "--health-addr",
                    $"127.0.0.1:{port}", "--otlp-endpoint", receiver.Url],
                TextWriter.Null, errors, _ => Task.FromException(new IOException(Secret)), token));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("The controller could not start or continue safely.", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, output.ToString(), StringComparison.Ordinal);
        using var console = JsonDocument.Parse(output.ToString());
        Assert.Equal("Error", console.RootElement.GetProperty("Level").GetString());
        var fields = console.RootElement.GetProperty("Properties");
        Assert.Equal("run", fields.GetProperty("operation").GetString());
        Assert.Equal("failed", fields.GetProperty("outcome").GetString());
        Assert.Equal("runtime_failure", fields.GetProperty("reason").GetString());
        var payload = Assert.Single(receiver.Requests, static request => IsSignal(request, "logs")).Payload();
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(payload), StringComparison.Ordinal);
        var log = new OtlpMessage(payload).Message(1).Message(2).Message(2);
        Assert.Equal("runtime_failure", log.StringAttributes(6)["reason"]);
    }

    [Fact]
    public async Task RuntimeOutageLogsAreThrottledPerProfileAndKeepTheActiveTrace()
    {
        var token = TestContext.Current.CancellationToken;
        var configuration = RuntimeConfigurationLoader.Load(await MixedRuntimeAsync(token));
        var first = Assert.IsType<TelemetryProfile>(TelemetryProfile.From(configuration.Profiles[0]));
        var second = Assert.IsType<TelemetryProfile>(TelemetryProfile.From(configuration.Profiles[1]));
        await using var receiver = await OtlpLoopback.StartAsync(grpc: false, respond: null, token);
        var settings = TelemetrySettings.Load(runtime: configuration, readEnvironment: name => name switch
        {
            "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" => receiver.Url + "/v1/logs",
            "OTEL_EXPORTER_OTLP_LOGS_PROTOCOL" => "http/protobuf",
            _ => null,
        });
        var capture = new CapturingConsole();
        await using var pipeline = TelemetryPipeline.Start(settings, TextWriter.Null, capture);
        var clock = new RuntimeLogClock();
        var runtime = new ControllerRuntimeLog(pipeline.Logging.CreateLogger(Controller), clock);
        using var trace = new Activity("fixture").SetIdFormat(ActivityIdFormat.W3C).Start();
        runtime.Write(LogLevel.Warning, "reconcile", "unavailable", "github_unavailable", first, throttle: true);
        clock.Now = TimeSpan.FromSeconds(29);
        runtime.Write(LogLevel.Warning, "reconcile", "unavailable", "github_unavailable", first, throttle: true);
        runtime.Write(LogLevel.Warning, "reconcile", "unavailable", "github_unavailable", second, throttle: true);
        clock.Now = TimeSpan.FromSeconds(30);
        runtime.Write(LogLevel.Warning, "reconcile", "unavailable", "github_unavailable", first, throttle: true);
        Assert.True(await pipeline.StopAsync().WaitAsync(token));

        Assert.Equal(new[] { first.Id, second.Id, first.Id }, capture.Events.Select(log => TelemetryPrivacy.Scalar(log, "profile")));
        Assert.All(capture.Events, log =>
        {
            Assert.Equal(trace.TraceId, log.TraceId);
            Assert.Equal(trace.SpanId, log.SpanId);
            Assert.Equal("github_unavailable", TelemetryPrivacy.Scalar(log, "reason"));
        });
        var records = receiver.Requests.SelectMany(request => new OtlpMessage(request.Payload()).Message(1).Message(2).Messages(2)).ToArray();
        Assert.Equal(new[] { first.Id, second.Id, first.Id }, records.Select(record => record.StringAttributes(6)["profile"]));
        Assert.All(records, record =>
        {
            Assert.Equal(trace.TraceId.ToHexString(), Convert.ToHexStringLower(record.Bytes(9)));
            Assert.Equal(trace.SpanId.ToHexString(), Convert.ToHexStringLower(record.Bytes(10)));
            Assert.Equal("github_unavailable", record.StringAttributes(6)["reason"]);
        });
    }

    private static Task<byte[]> MixedRuntimeAsync(CancellationToken token) =>
        File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "mixed.json"), token);

    [LoggerMessage(2, LogLevel.Error, "Unexpected failure {payload}")]
    private static partial void WriteUnknownEvent(ILogger logger, string payload, Exception exception);

    private sealed class RuntimeLogClock : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        internal TimeSpan Now { get; set; }

        public override long GetTimestamp() => Now.Ticks;
    }

    private sealed class RotatingSecrets()
        : AmazonSecretsManagerClient(new BasicAWSCredentials("fixture", "fixture"), RegionEndpoint.APSoutheast2)
    {
        private int _reads;

        public override Task<GetSecretValueResponse> GetSecretValueAsync(GetSecretValueRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_reads++ != 0)
            {
                return Task.FromException<GetSecretValueResponse>(
                    new HttpRequestException($"{Secret} {request.SecretId}"));
            }

            using var key = RSA.Create(2048);
            return Task.FromResult(new GetSecretValueResponse
            {
                HttpStatusCode = HttpStatusCode.OK,
                ARN = request.SecretId,
                VersionId = new string('a', 32),
                VersionStages = ["AWSCURRENT"],
                SecretString = JsonSerializer.Serialize(new
                {
                    clientId = Secret,
                    installationId = 1,
                    privateKey = key.ExportRSAPrivateKeyPem(),
                }),
            });
        }
    }
}
