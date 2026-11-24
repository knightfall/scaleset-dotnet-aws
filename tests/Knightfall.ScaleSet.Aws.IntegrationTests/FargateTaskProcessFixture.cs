using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed class FargateMetadataFixture(WebApplication app, Uri endpoint) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WireNames = new();

    internal Uri ForTask(string taskArn) => new(endpoint, "v4/" + ShortId(taskArn));

    internal static async Task<FargateMetadataFixture> StartAsync(
        IReadOnlyCollection<ExecutorBinding> bindings, string family, CancellationToken token)
    {
        var tasks = bindings.ToDictionary(binding => ShortId(binding.ResourceArn), StringComparer.Ordinal);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var host = builder.Build();
        _ = host.MapGet("/v4/{id}", (string id) => !tasks.ContainsKey(id) ?
            Results.NotFound() : Results.Json(new { DockerId = ContainerId(id), Name = FargateNativeFixture.Runner }, WireNames));
        _ = host.MapGet("/v4/{id}/task", (string id) =>
        {
            if (!tasks.TryGetValue(id, out var binding) || binding.Provider is not FargateExecutorIdentity provider)
            {
                return Results.NotFound();
            }
            return Results.Json(new
            {
                Cluster = provider.ClusterArn,
                TaskARN = binding.ResourceArn,
                Family = family,
                Revision = provider.TaskDefinitionArn[(provider.TaskDefinitionArn.LastIndexOf(':') + 1)..],
                LaunchType = "FARGATE",
                Containers = new[] { new { DockerId = ContainerId(id), Name = FargateNativeFixture.Runner } },
            }, WireNames);
        });
        try
        {
            await host.StartAsync(token);
            return new FargateMetadataFixture(host, new Uri(Assert.Single(host.Urls)));
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static string ContainerId(string id) => id + "-0000000001";

    internal static string ShortId(string taskArn) => taskArn[(taskArn.LastIndexOf('/') + 1)..];

    public ValueTask DisposeAsync() => app.DisposeAsync();
}

internal sealed class SimulatedFargateJob(string expectedRunnerName) : IOneJobRunner
{
    internal const string ChildMode = "--t24-simulated-task-process";
    private const string Ready = "fixture task connected";
    private const string Finish = "finish one fixture job";
    private readonly TaskCompletionSource<long> _runnerId = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Process> _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<Process> Running => _running.Task;

    internal void BindRunner(long id) => _runnerId.SetResult(id);

    internal void AllowExit() => _finish.TrySetResult();

    internal static async Task<int> RunChildAsync()
    {
        await Console.Out.WriteLineAsync(Ready);
        return await Console.In.ReadLineAsync() == Finish ? 0 : 2;
    }

    public async Task<OneJobCompletion> RunAsync(JitPlaintext configuration, string expectedName,
        Func<long, CancellationToken, Task> ready, CancellationToken cancellationToken)
    {
        Assert.True(configuration.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(GitHubSimulator.Jit)));
        Assert.Equal(expectedRunnerName, expectedName);
        var id = await _runnerId.Task.WaitAsync(cancellationToken);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(SimulatedFargateJob).Assembly.Location);
        start.ArgumentList.Add(ChildMode);
        foreach (string key in start.Environment.Keys.Where(static key =>
            key.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ACTIONS_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _ = start.Environment.Remove(key);
        }
        Assert.All(start.ArgumentList, argument => Assert.DoesNotContain(GitHubSimulator.Jit, argument, StringComparison.Ordinal));
        Assert.All(start.Environment.Values, value => Assert.DoesNotContain(GitHubSimulator.Jit, value, StringComparison.Ordinal));

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The fixture worker failed to start.");
        try
        {
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            Assert.Equal(Ready, await process.StandardOutput.ReadLineAsync(cancellationToken));
            await ready(id, cancellationToken);
            _running.SetResult(process);
            await _finish.Task.WaitAsync(cancellationToken);
            await process.StandardInput.WriteLineAsync(Finish.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await errors);
            Assert.Empty(await process.StandardOutput.ReadToEndAsync(cancellationToken));
            return new OneJobCompletion(id, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
