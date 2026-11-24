using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

[SupportedOSPlatform("linux")]
internal interface ILinuxOneJobBoundary
{
    void WriteConfigFile(string name, ReadOnlySpan<byte> contents);

    void PrepareHome();

    ProcessStartInfo StartInfo();

    void RemoveConfigFiles();
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxOneJobRunner(ILinuxOneJobBoundary boundary) : IOneJobRunner
{
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
            using var process = Process.Start(boundary.StartInfo()) ?? throw new IOException("The one-job runner could not start.");
            process.StandardInput.Close();
            using var output = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task stdout = ReadReadyAsync(process.StandardOutput, runnerId, ready, output.Token);
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, output.Token);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return new OneJobCompletion(runnerId, process.ExitCode);
            }
            finally
            {
                await output.CancelAsync().ConfigureAwait(false);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            boundary.RemoveConfigFiles();
        }
    }

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
