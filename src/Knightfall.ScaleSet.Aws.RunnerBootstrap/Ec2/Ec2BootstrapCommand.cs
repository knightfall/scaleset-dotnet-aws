using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

namespace Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;

[SupportedOSPlatform("linux")]
internal static class Ec2BootstrapCommand
{
    internal static async Task<int> RunAsync(string configurationPath)
    {
        using var stopping = new CancellationTokenSource();
        using var stop = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stopping.Cancel();
        });
        try
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
            var outcome = await new Ec2BootstrapApplication(settings, TimeProvider.System).RunAsync(stopping.Token);
            Console.WriteLine(outcome);
            return outcome is RunnerBootstrapOutcome.Completed or RunnerBootstrapOutcome.AlreadySpent ? 0 : 1;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            Console.Error.WriteLine("EC2 bootstrap stopped.");
            return 1;
        }
        catch (RunnerTeardownQualificationException)
        {
            Console.Error.WriteLine("EC2 supervisor STATUS requires a current boot proof of job process teardown.");
            return 1;
        }
        catch (RunnerCleanupIncompleteException)
        {
            Console.Error.WriteLine("EC2 runner cleanup was incomplete. Retain the instance for operator recovery.");
            return 1;
        }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException or Win32Exception or
                                      AmazonClientException or AmazonServiceException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("EC2 bootstrap failed.");
            return 1;
        }
    }
}
