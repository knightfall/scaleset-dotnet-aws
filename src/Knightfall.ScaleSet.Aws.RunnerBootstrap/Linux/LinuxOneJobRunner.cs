using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

[SupportedOSPlatform("linux")]
internal interface ILinuxOneJobBoundary
{
    bool IsolatedProcessTree => false;

    void WriteConfigFile(string name, ReadOnlySpan<byte> contents);

    void PrepareHome();

    Process? StartRunner();

    void RemoveConfigFiles();
}

[SupportedOSPlatform("linux")]
internal sealed partial class LinuxOneJobRunner(ILinuxOneJobBoundary boundary, TimeSpan? stopGrace = null) : IOneJobRunner
{
    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(5);

    public async Task<OneJobCompletion> RunAsync(JitPlaintext configuration, string expectedName,
        Func<long, CancellationToken, Task> ready, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long runnerId;
        try
        {
            try
            {
                using var settings = JitRunnerConfiguration.Decode(configuration.Bytes, expectedName);
                runnerId = settings.RunnerId;
                settings.Materialize(boundary);
            }
            finally
            {
                configuration.Dispose();
            }

            boundary.PrepareHome();
            using var process = boundary.StartRunner() ?? throw new IOException("The one-job runner could not start.");
            process.StandardInput.Close();
            using var output = new CancellationTokenSource();
            Task stdout = ReadReadyAsync(process.StandardOutput, runnerId, ready, output.Token);
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, output.Token);
            Task drain = Task.WhenAll(stdout, stderr);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await WaitForTeardownAsync(drain, cancellationToken).ConfigureAwait(false);
                if (boundary.IsolatedProcessTree && process.ExitCode >= 128)
                {
                    // A signaled launcher cannot attest that it waited for namespace teardown.
                    throw new RunnerCleanupIncompleteException();
                }
                return new OneJobCompletion(runnerId, process.ExitCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && stopGrace is { } grace)
            {
                if (!process.HasExited)
                {
                    const int sigterm = 15;
                    _ = SendSignal(process.Id, sigterm);
                }
                try
                {
                    await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), drain)
                        .WaitAsync(grace, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The finalizer below kills the remaining process tree if graceful exit timed out.
                }
                throw;
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        // The trusted namespace launcher kills PID 1 on parent death. The kernel
                        // then shuts down that namespace, including reparented and detached children.
                        process.Kill(entireProcessTree: !boundary.IsolatedProcessTree);
                        await WaitForTeardownAsync(process.WaitForExitAsync(CancellationToken.None), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await WaitForTeardownAsync(drain, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    output.Cancel();
                    process.StandardOutput.Dispose();
                    process.StandardError.Dispose();
                    _ = drain.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        finally
        {
            boundary.RemoveConfigFiles();
        }
    }

    private static async Task WaitForTeardownAsync(Task operation, CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(TeardownBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RunnerCleanupIncompleteException();
        }
    }

    [LibraryImport("libc", EntryPoint = "kill")]
    private static partial int SendSignal(int processId, int signal);

    internal static async Task ReadReadyAsync(TextReader stdout, long runnerId, Func<long, CancellationToken, Task> ready,
        CancellationToken cancellationToken)
    {
        var input = new char[512];
        var line = new char[96];
        int length = 0;
        bool reported = false;
        int read;
        while ((read = await stdout.ReadAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (int index = 0; index < read; index++)
            {
                char value = input[index];
                if (value == '\n')
                {
                    if (!reported && length <= line.Length && IsReadyLine(line.AsSpan(0, length)))
                    {
                        reported = true;
                        await ready(runnerId, cancellationToken).ConfigureAwait(false);
                    }
                    length = 0;
                }
                else if (length <= line.Length)
                {
                    if (length < line.Length)
                    {
                        line[length] = value;
                    }
                    length++;
                }
            }
        }
    }

    private static bool IsReadyLine(ReadOnlySpan<char> line)
    {
        line = line.TrimEnd('\r');
        const string marker = ": Listening for Jobs";
        return line.EndsWith(marker, StringComparison.Ordinal) &&
            DateTimeOffset.TryParseExact(line[..^marker.Length], "u", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out _);
    }
}

internal sealed class RunnerCleanupIncompleteException() :
    IOException("The one-job runner process or credential cleanup could not be confirmed.");
