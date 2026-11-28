using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;
using Microsoft.Extensions.Logging;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;

internal static class Ec2BootstrapCommand
{
    [SupportedOSPlatform("linux")]
    internal static async Task<int> RunAsync(string configurationPath)
    {
        using var stopping = new CancellationTokenSource();
        using var stop = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });
        return await RunWithDiagnosticsAsync(RunBootstrapAsync, Console.Out, Console.Error, TimeProvider.System, stopping.Token)
            .ConfigureAwait(false);

        async Task<RunnerBootstrapOutcome> RunBootstrapAsync(ILogger? logger, CancellationToken token)
        {
            if (!LinuxFiles.IsRoot() || !LinuxFiles.IsTrustedFile(configurationPath, ownerOnly: true))
            {
                throw new IOException("The EC2 bootstrap settings file is not trusted.");
            }

            LinuxFiles.DisableCoreDumps();
            await using var document = File.OpenRead(configurationPath);
            if (document.Length is <= 0 or > 16 * 1024)
            {
                throw new IOException("The EC2 bootstrap settings file has an invalid size.");
            }
            var settings = JsonSerializer.Deserialize<Ec2BootstrapSettings>(document, BootstrapJson.Options) ??
                throw new IOException("The EC2 bootstrap settings file is empty.");
            return await new Ec2BootstrapApplication(settings, TimeProvider.System, logger).RunAsync(token).ConfigureAwait(false);
        }
    }

    internal static async Task<int> RunWithDiagnosticsAsync(Func<ILogger?, CancellationToken, Task<RunnerBootstrapOutcome>> run,
        TextWriter output, TextWriter errors, TimeProvider clock, CancellationToken stopping)
    {
        await using var logger = BootstrapLogQueue.Start(new BootstrapConsoleLogger(output, errors), clock);
        try
        {
            var outcome = await run(logger, stopping).ConfigureAwait(false);
            BootstrapLog.Write(logger, target => BootstrapLog.Outcome(target, outcome));
            return outcome is RunnerBootstrapOutcome.Completed or RunnerBootstrapOutcome.AlreadySpent ? 0 : 1;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            BootstrapLog.Write(logger, BootstrapLog.Ec2Stopped);
            return 1;
        }
        catch (RunnerTeardownQualificationException)
        {
            BootstrapLog.Write(logger, BootstrapLog.Ec2TeardownUnqualified);
            return 1;
        }
        catch (RunnerCleanupIncompleteException)
        {
            BootstrapLog.Write(logger, BootstrapLog.Ec2CleanupIncomplete);
            return 1;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or HttpRequestException or ArgumentException or JsonException or Win32Exception or
                                      AmazonClientException or AmazonServiceException or InvalidOperationException or UnauthorizedAccessException or
                                      OperationCanceledException)
        {
            BootstrapLog.Write(logger, BootstrapLog.Ec2Failed);
            return 1;
        }
    }
}
