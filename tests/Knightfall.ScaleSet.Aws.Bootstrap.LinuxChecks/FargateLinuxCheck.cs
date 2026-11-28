using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
    private const string SettingsPath = "/opt/scaleset/fargate-bootstrap.json";
    private static readonly FargateBootstrapSettings Settings =
        JsonSerializer.Deserialize<FargateBootstrapSettings>(File.ReadAllText(SettingsPath), BootstrapJson.Options)!;

    internal static async Task<int> VerifyProductionMetadataRejectionAsync()
    {
        if (!LinuxFiles.HasFargateRunnerIdentity() || !LinuxFiles.IsTrustedFile(SettingsPath, ownerOnly: false))
            throw new InvalidOperationException("The fixture requires valid production bootstrap settings and identity.");
        _ = Settings.Validate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Environment.SetEnvironmentVariable("ECS_CONTAINER_METADATA_URI_V4", $"http://127.0.0.1:{port}/v4/fixture");
        Environment.SetEnvironmentVariable("AWS_CONTAINER_CREDENTIALS_RELATIVE_URI", "/v2/credentials/" + Guid.NewGuid().ToString("D"));
        int result = await FargateBootstrapCommand.RunAsync(SettingsPath);
        if (result != 1 || listener.Pending())
            throw new InvalidOperationException("The production command did not reject loopback before transport.");
        return 0;
    }

    internal static async Task<int> RunAsync()
    {
        if (!LinuxFiles.HasFargateRunnerIdentity() || Environment.ProcessId != 1)
        {
            throw new InvalidOperationException("The Fargate fixture must run as non-root PID 1.");
        }
        LinuxFiles.DisableCoreDumps();
        VerifyRunnerIdentity();
        VerifyReadOnlyMountEvidence();
        VerifyReadOnlyCleanup();
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
        Console.WriteLine("Fargate offline PID 1, read-only mount evidence, RAM cleanup, pinned launcher, thread-hop privilege guard, one-job and SIGTERM checks passed.");
        return 0;
    }

    private static void VerifyRunnerIdentity()
    {
        const string user = "Uid: 1000 1000 1000 1000";
        const string group = "Gid: 2147483647 2147483647 2147483647 2147483647";
        string[] capabilities = ["CapInh:", "CapPrm:", "CapEff:", "CapBnd:", "CapAmb:"];
        string[] dropped = [.. capabilities.Select(field => field + " 0000000000000000")];
        if (!LinuxFiles.HasFargateRunnerIdentity([user, group, "Groups:", .. dropped]) ||
            !LinuxFiles.HasFargateRunnerIdentity([user, group, "Groups: 1000 2147483647", .. dropped]) ||
            LinuxFiles.HasFargateRunnerIdentity(["Uid: 1000 1000 1000 2147483648", group, "Groups:", .. dropped]) ||
            LinuxFiles.HasFargateRunnerIdentity([user, "Gid: 0 1000 1000 1000", "Groups:", .. dropped]) ||
            LinuxFiles.HasFargateRunnerIdentity([user, "Gid: 1000 999 1000 1000", "Groups:", .. dropped]) ||
            LinuxFiles.HasFargateRunnerIdentity([user, group, "Groups: 1000 999", .. dropped]) ||
            LinuxFiles.HasFargateRunnerIdentity([user, group, "Groups: 2147483648", .. dropped]) ||
            LinuxFiles.HasFargateRunnerIdentity([user, group, .. dropped]))
        {
            throw new InvalidOperationException("The runtime identity check did not enforce the supported ID range.");
        }
        foreach (string field in capabilities)
        {
            string[] others = [.. dropped.Where(entry => !entry.StartsWith(field, StringComparison.Ordinal))];
            if (LinuxFiles.HasFargateRunnerIdentity([user, group, "Groups:", .. others]) ||
                LinuxFiles.HasFargateRunnerIdentity([user, group, "Groups:", .. others, field]) ||
                LinuxFiles.HasFargateRunnerIdentity([user, group, "Groups:", .. others, field + " malformed"]) ||
                LinuxFiles.HasFargateRunnerIdentity([user, group, "Groups:", .. others, field + " 0000000000000001"]))
            {
                throw new InvalidOperationException("The runtime identity check accepted missing, malformed, or retained capabilities.");
            }
        }
    }

    private static void VerifyReadOnlyMountEvidence()
    {
        const string path = "/opt/scaleset/runner";
        string[] mounts = ["21 10 0:1 / / ro,relatime - overlay overlay ro",
            "31 21 0:2 / /opt/scaleset ro,relatime - overlay overlay ro"];
        const string writable = "41 21 0:3 / /opt/scaleset rw,relatime - tmpfs data rw";
        const string sameReadOnly = "42 21 0:4 / /opt/scaleset ro,relatime - overlay overlay ro";
        if (!FargateMountLayout.IsReadOnly(path, mounts, effectiveMountId: 31) ||
            FargateMountLayout.IsReadOnly(path, mounts, effectiveMountId: null) ||
            FargateMountLayout.IsReadOnly(path, mounts, effectiveMountId: 21) ||
            FargateMountLayout.IsReadOnly(path, [.. mounts, writable], effectiveMountId: 41) ||
            FargateMountLayout.IsReadOnly(path, [writable, .. mounts], effectiveMountId: 31) ||
            FargateMountLayout.IsReadOnly(path, [.. mounts, sameReadOnly], effectiveMountId: 42) ||
            !FargateMountLayout.IsReadOnly(path, File.ReadAllLines("/proc/self/mountinfo"), LinuxFiles.GetMountId(path)))
        {
            throw new InvalidOperationException("The fixture did not require unambiguous effective read-only mount evidence.");
        }
    }

    private static void VerifyReadOnlyCleanup()
    {
        const UnixFileMode readable = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        using var boundary = FargateRunnerBoundary.Create(Settings);
        string root = boundary.StartInfo().WorkingDirectory;
        string sentinel = Path.Combine(new DirectoryInfo(Path.Combine(root, "_work")).LinkTarget!, "outside-ram");
        string nested = Path.Combine(root, "home", "nested");
        Directory.CreateDirectory(sentinel);
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(sentinel, "marker"), "untouched");
        File.WriteAllText(Path.Combine(nested, ".credentials"), "offline-cleanup-fixture");
        Directory.CreateSymbolicLink(Path.Combine(nested, "outside"), sentinel);
        Directory.CreateSymbolicLink(Path.Combine(nested, "cycle"), root);
        File.CreateSymbolicLink(Path.Combine(nested, "dangling"), Path.Combine(root, "missing"));
        File.SetUnixFileMode(sentinel, readable);
        File.SetUnixFileMode(nested, UnixFileMode.None);
        File.SetUnixFileMode(Path.Combine(root, "home"), readable);
        File.SetUnixFileMode(root, readable);
        boundary.Dispose();
        boundary.Dispose();

        if (Directory.Exists(root) || File.GetUnixFileMode(sentinel) != readable ||
            File.ReadAllText(Path.Combine(sentinel, "marker")) != "untouched")
        {
            throw new InvalidOperationException("The fixture left its RAM root or modified a symlink target.");
        }
        File.SetUnixFileMode(sentinel, readable | UnixFileMode.UserWrite);
        Directory.Delete(sentinel, recursive: true);
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
