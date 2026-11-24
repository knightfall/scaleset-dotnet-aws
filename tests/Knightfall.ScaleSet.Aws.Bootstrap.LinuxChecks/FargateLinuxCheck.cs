using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Fargate;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

namespace Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks;

[SupportedOSPlatform("linux")]
internal static class FargateLinuxCheck
{
    private const string TaskArn = "arn:aws:ecs:ap-southeast-2:111122223333:task/fixture/00112233445566778899aabbccddeeff";
    private const string User = "AROAFIXTURETASK000001:00112233445566778899aabbccddeeff";
    private const string Name = "ss-fixture";
    private const string Work = "/work";
    private static readonly FargateBootstrapSettings Settings = new("111122223333", "ap-southeast-2",
        "111122223333:ap-southeast-2:fixture", "runner", "https://github.com/fixture/repo", "fixture",
        "arn:aws:ecs:ap-southeast-2:111122223333:cluster/fixture", "fixture", "runner",
        "arn:aws:iam::111122223333:role/fixture", "AROAFIXTURETASK000001", "/opt/scaleset/runner",
        "/opt/scaleset/runner-dotnet/dotnet", "/opt/scaleset/runner-child", Work);

    internal static async Task<int> RunAsync()
    {
        if (LinuxFiles.IsRoot())
        {
            throw new InvalidOperationException("The Fargate fixture must be non-root.");
        }
        LinuxFiles.DisableCoreDumps();
        Environment.SetEnvironmentVariable("AWS_SESSION_TOKEN", "offline-parent-credential-sentinel");
        Environment.SetEnvironmentVariable("ACTIONS_RUNNER_INPUT_JITCONFIG", "offline-parent-jit-sentinel");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await VerifySingleJobAfterThreadHopAsync(budget.Token).ConfigureAwait(false);
        await VerifyPinnedRunnerAsync(budget.Token).ConfigureAwait(false);
        await VerifySingleJobAsync(interrupt: true, budget.Token).ConfigureAwait(false);
        var receipts = Directory.GetFiles(Work, "*", SearchOption.AllDirectories);
        string[] expectedReceipts = ["completed\n", "signal\n"];
        if (receipts.Length != 2 || receipts.Any(file => Path.GetFileName(file) != "receipt") ||
            !receipts.Select(File.ReadAllText).Order(StringComparer.Ordinal)
                .SequenceEqual(expectedReceipts, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The fixture job or SIGTERM was not observed in its separate work scratch.");
        }
        Console.WriteLine("Fargate offline RAM, pinned launcher, thread-hop privilege guard, one-job and SIGTERM checks passed.");
        return 0;
    }

    private static Task VerifySingleJobAfterThreadHopAsync(CancellationToken cancellationToken)
    {
        var startupGuarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launch = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                startupGuarded.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                if (!File.ReadLines("/proc/thread-self/status").Contains("NoNewPrivs:\t0", StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("The pre-existing launch thread must start without no-new-privileges.");
                }
                // RunAsync starts the actual child synchronously on this thread before its first await.
                launch.SetResult(VerifySingleJobAsync(interrupt: false, cancellationToken));
            }
            catch (Exception error)
            {
                launch.SetException(error);
            }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            LinuxFiles.ProhibitPrivilegeGain();
            startupGuarded.SetResult();
        }
        catch (IOException error)
        {
            startupGuarded.SetException(error);
        }
        return launch.Task.Unwrap();
    }

    private static async Task VerifyPinnedRunnerAsync(CancellationToken cancellationToken)
    {
        using var boundary = FargateRunnerBoundary.Create(Settings);
        boundary.PrepareHome();
        var listener = boundary.StartInfo();
        if (!listener.WorkingDirectory.StartsWith("/dev/shm/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The configuration root escaped the verified RAM mount.");
        }
        listener.ArgumentList.RemoveAt(listener.ArgumentList.Count - 1);
        listener.ArgumentList.RemoveAt(listener.ArgumentList.Count - 1);
        listener.ArgumentList.Add("--version");
        var launch = await RunProcessAsync(listener, cancellationToken).ConfigureAwait(false);
        if (launch.ExitCode != 0 || !launch.Output.Split('\n').Contains("2.338.2", StringComparer.Ordinal) ||
            !HasDiagnostic(listener.WorkingDirectory, "Runner_*", "d1eb12d4b115c301201de27d667284e1a80f2ab5"))
        {
            throw new InvalidOperationException("The exact pinned listener did not start within the RAM root.");
        }

        var worker = await LaunchChildAsync(boundary, "Runner.Worker", cancellationToken).ConfigureAwait(false);
        if (worker.ExitCode != 1 || !HasDiagnostic(listener.WorkingDirectory, "Worker_*", "Version: 2.338.2"))
        {
            throw new InvalidOperationException("The pinned worker did not start within the RAM root.");
        }

        var plugin = await LaunchChildAsync(boundary, "Runner.PluginHost", cancellationToken).ConfigureAwait(false);
        if (plugin.ExitCode != 1 || !plugin.Error.Contains("GitHub.Runner.PluginHost.Program.Main", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The pinned plugin host was not reached.");
        }
    }

    private static bool HasDiagnostic(string root, string pattern, string expected)
    {
        string path = Path.Combine(root, "_diag");
        return Directory.Exists(path) && Directory.EnumerateFiles(path, pattern)
            .Any(file => File.ReadAllText(file).Contains(expected, StringComparison.Ordinal));
    }

    private static Task<ProcessResult> LaunchChildAsync(FargateRunnerBoundary boundary, string child, CancellationToken cancellationToken)
    {
        var start = boundary.StartInfo();
        start.FileName = Path.Combine(start.WorkingDirectory, "bin", child);
        start.ArgumentList.Clear();
        return RunProcessAsync(start, cancellationToken);
    }

    private static async Task<ProcessResult> RunProcessAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The offline runner process could not start.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task VerifySingleJobAsync(bool interrupt, CancellationToken cancellationToken)
    {
        string fixtureHost = interrupt ? "/opt/scaleset/fixture-dotnet-stop" : "/opt/scaleset/fixture-dotnet";
        using var boundary = FargateRunnerBoundary.Create(Settings with { RunnerDotnet = fixtureHost });
        var root = boundary.StartInfo().WorkingDirectory;
        using var key = JitRecipientKey.Create();
        var now = TimeProvider.System;
        var identity = new JitHandoffIdentity(Settings.Deployment, "runner", "github.com/fixture/repo", 1, Guid.NewGuid(), TaskArn, Guid.NewGuid(), User);
        var binding = JitOfferBinding.Create(identity, key.Hello.BootNonce, now.GetUtcNow().AddMinutes(1));
        var settings = JsonSerializer.SerializeToUtf8Bytes(new { AgentId = 17L, AgentName = Name, Ephemeral = true, WorkFolder = "_work" });
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".runner"] = Convert.ToBase64String(settings),
            [".credentials"] = Convert.ToBase64String("offline-fargate-jit-sentinel"u8),
            [".credentials_rsaparams"] = Convert.ToBase64String("synthetic-rsa-fixture"u8),
        });
        var sealedOffer = JitOfferCryptography.Seal(binding, key.Hello, Encoding.UTF8.GetBytes(Convert.ToBase64String(document)), now);
        using var plaintext = key.Open(sealedOffer, identity, now);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runner = new LinuxOneJobRunner(boundary, stopGrace: TimeSpan.FromSeconds(5));
        int readyCount = 0;
        try
        {
            var result = await runner.RunAsync(plaintext, Name, (id, _) =>
            {
                if (id != 17)
                {
                    throw new InvalidOperationException("The fixture authenticated the wrong runner.");
                }
                readyCount++;
                if (interrupt)
                {
                    stopped.Cancel();
                }
                return Task.CompletedTask;
            }, stopped.Token).ConfigureAwait(false);
            if (interrupt || result != new OneJobCompletion(17, 0))
            {
                throw new InvalidOperationException("The fixture did not report the one-job outcome.");
            }
        }
        catch (OperationCanceledException) when (interrupt && stopped.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        if (Directory.Exists(root) || readyCount != 1)
        {
            throw new InvalidOperationException("The one-job runner did not release its RAM root or reported duplicate readiness.");
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
