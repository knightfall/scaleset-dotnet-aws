using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Controller.Commands;
using Knightfall.ScaleSet.Aws.Controller.Health;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class ControllerHealthTests
{
    [Fact]
    public async Task LoopbackProbeUsesLivenessAndReadinessShowsLeaseAndProfileDegradation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var snapshot = new ControllerHealthSnapshot(true, true, true, false,
            [new("linux-ec2", true), new("linux-fargate", false)]);
        await using var host = await ControllerHealthServer.StartAsync(
            new LoopbackHealthAddress(IPAddress.Loopback, 0), () => Volatile.Read(ref snapshot), cancellationToken);

        var address = new Uri(Assert.Single(host.Urls));
        Assert.Equal(IPAddress.Loopback.ToString(), address.Host);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = address };
        string probeAddress = $"127.0.0.1:{address.Port}";

        var liveProbe = await RunProbeAsync(probeAddress, cancellationToken);
        Assert.Equal(0, liveProbe.ExitCode);
        Assert.Equal("healthy", liveProbe.Output.TrimEnd());
        Assert.Empty(liveProbe.Error);

        using (var beforeLease = await client.GetAsync("readyz", cancellationToken))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, beforeLease.StatusCode);
            using var body = JsonDocument.Parse(await beforeLease.Content.ReadAsStringAsync(cancellationToken));
            Assert.True(body.RootElement.GetProperty("configurationVerified").GetBoolean());
            Assert.False(body.RootElement.GetProperty("leaseActive").GetBoolean());
            Assert.Collection(body.RootElement.GetProperty("profiles").EnumerateArray(),
                row =>
                {
                    Assert.Equal("linux-ec2", row.GetProperty("profile").GetString());
                    Assert.True(row.GetProperty("ready").GetBoolean());
                },
                row =>
                {
                    Assert.Equal("linux-fargate", row.GetProperty("profile").GetString());
                    Assert.False(row.GetProperty("ready").GetBoolean());
                });
        }

        Volatile.Write(ref snapshot, snapshot with { LeaseActive = true });
        using var degraded = await client.GetAsync("readyz", cancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, degraded.StatusCode);

        Volatile.Write(ref snapshot, snapshot with { Profiles = [new("linux-ec2", true), new("linux-fargate", true)] });
        using var ready = await client.GetAsync("readyz", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        Volatile.Write(ref snapshot, snapshot with { Live = false });
        var failedProbe = await RunProbeAsync(probeAddress, cancellationToken);
        Assert.Equal(1, failedProbe.ExitCode);
        Assert.Empty(failedProbe.Output);
        Assert.Contains("health probe failed", failedProbe.Error, StringComparison.Ordinal);
        await host.StopAsync(cancellationToken);
    }

    [Theory]
    [InlineData("0.0.0.0:8080")]
    [InlineData("[::]:8080")]
    [InlineData("10.0.0.1:8080")]
    [InlineData("localhost:8080")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("127.0.0.1:8080/")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    public void RejectsAnyProbeBindingWithoutAnExplicitNumericLoopbackPort(string input) =>
        Assert.Throws<ArgumentException>(() => LoopbackHealthAddress.Parse(input));

    [Fact]
    public void UsesTheSameDefaultAddressForHostAndEcsProbe() =>
        Assert.Equal("http://127.0.0.1:8080/healthz", LoopbackHealthAddress.Parse(null).ProbeUri.AbsoluteUri);

    [Fact]
    public void ReadOnlyReadinessNeedsVerifiedConfigWithoutRequiringLeaseOwnership()
    {
        var snapshot = new ControllerHealthSnapshot(true, true, false, false, [new("linux-ec2", true)]);

        Assert.True(snapshot.Ready);
        Assert.False((snapshot with { ConfigurationVerified = false }).Ready);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProbeAsync(
        string address, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The OCI acceptance can exercise the exact image entry point from its read-only root filesystem.
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("SCALESET_HEALTHCHECK_ASSEMBLY") ??
            typeof(HealthcheckCommand).Assembly.Location);
        start.ArgumentList.Add("healthcheck");
        start.ArgumentList.Add("--health-addr");
        start.ArgumentList.Add(address);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }
}
