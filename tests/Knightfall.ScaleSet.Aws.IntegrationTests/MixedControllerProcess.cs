using System.Diagnostics;
using System.Globalization;
using System.Net;
using Knightfall.ScaleSet.Aws.Controller.Configuration;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed class MixedControllerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _output;
    private readonly Task<string> _errors;
    private readonly HttpClient _health = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(1),
    };
    private readonly Uri _probe;

    private MixedControllerProcess(Process process, int port)
    {
        _process = process;
        _probe = new Uri(FormattableString.Invariant($"http://127.0.0.1:{port}/readyz"));
        _output = process.StandardOutput.ReadToEndAsync();
        _errors = process.StandardError.ReadToEndAsync();
    }

    internal static MixedControllerProcess Start(MixedRuntimeFixture fixture, string taskArn, string? telemetryEndpoint)
    {
        int port = MixedRuntimeFixture.FreePort();
        var command = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, typeof(MixedControllerProcess).Assembly.GetName().Name!))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string[] arguments =
        [
            ControllerIntegrationEntryPoint.MixedChildMode, fixture.GitHub.Port.ToString(CultureInfo.InvariantCulture), fixture.GitHub.Fingerprint,
            "run", "--config", fixture.ConfigurationPath, "--sha256", fixture.Configuration.Sha256, "--generation", "1", "--health-addr",
            FormattableString.Invariant($"127.0.0.1:{port}"),
        ];
        foreach (string argument in arguments)
        {
            command.ArgumentList.Add(argument);
        }

        foreach (string key in command.Environment.Keys.Where(key =>
            key.StartsWith("OTEL_", StringComparison.Ordinal) || key.StartsWith("AWS_", StringComparison.Ordinal)).ToArray())
        {
            command.Environment.Remove(key);
        }
        command.Environment["AWS_REGION"] = MixedControllerCloud.Region;
        command.Environment["AWS_ACCESS_KEY_ID"] = MixedControllerCloud.Account;
        command.Environment["AWS_SECRET_ACCESS_KEY"] = MixedControllerCloud.Credentials.GetCredentials().SecretKey;
        command.Environment["AWS_ENDPOINT_URL"] = fixture.Cloud.Endpoint.AbsoluteUri;
        command.Environment["AWS_EC2_METADATA_DISABLED"] = "true";
        command.Environment["ECS_CONTAINER_METADATA_URI_V4"] = fixture.Cloud.MetadataUri(taskArn).AbsoluteUri;
        command.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        command.Environment["no_proxy"] = "127.0.0.1,localhost";
        command.Environment[FargateRuntimeEvidence.EnvironmentVariable] = fixture.FargateEvidence;
        command.Environment["OTEL_SDK_DISABLED"] = telemetryEndpoint is null ? "true" : "false";
        if (telemetryEndpoint is not null)
        {
            command.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = telemetryEndpoint;
            command.Environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
            command.Environment["OTEL_EXPORTER_OTLP_HEADERS"] = "authorization=Bearer%20mixed-synthetic-collector";
            command.Environment["OTEL_EXPORTER_OTLP_TIMEOUT"] = "1000";
            command.Environment["OTEL_METRIC_EXPORT_INTERVAL"] = "1000";
            command.Environment["OTEL_TRACES_SAMPLER"] = "parentbased_always_on";
        }
        return new MixedControllerProcess(Process.Start(command) ?? throw new InvalidOperationException("Cannot start the mixed worker."), port);
    }

    internal async Task<HttpStatusCode?> ReadyAsync(CancellationToken token)
    {
        if (_process.HasExited)
        {
            string diagnostics = await DiagnosticsAsync();
            throw new InvalidOperationException($"The mixed worker exited with code {_process.ExitCode}. {diagnostics}");
        }
        try
        {
            using var response = await _health.GetAsync(_probe, token);
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    internal async Task KillAsync(CancellationToken token)
    {
        if (!_process.HasExited)
        {
            _process.Kill();
        }
        await _process.WaitForExitAsync(token);
    }

    internal async Task<int> ExitCodeAsync(CancellationToken token)
    {
        await _process.WaitForExitAsync(token);
        return _process.ExitCode;
    }

    internal async Task<string> DiagnosticsAsync() => await _output + await _errors;

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await KillAsync(timeout.Token);
        _health.Dispose();
        _process.Dispose();
    }
}
