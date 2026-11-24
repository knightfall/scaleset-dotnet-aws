using System.Globalization;
using Knightfall.ScaleSet.Aws.Controller.Commands;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Xunit.MicrosoftTestingPlatform;
using Xunit.Runner.InProc.SystemConsole;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal static class ControllerIntegrationEntryPoint
{
    internal const string ChildMode = "--controller-host-fixture";

    public static async Task<int> Main(string[] args)
    {
        if (args is [ChildMode, var portValue, var fingerprint, "run", ..])
        {
            if (!ushort.TryParse(portValue, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) || port == 0 ||
                fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit) ||
                !Uri.TryCreate(Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL"), UriKind.Absolute, out var aws) ||
                aws.Scheme != Uri.UriSchemeHttp || aws.Host != "127.0.0.1" || aws.IsDefaultPort || aws.AbsolutePath != "/" ||
                aws.UserInfo.Length != 0 || aws.Query.Length != 0 || aws.Fragment.Length != 0)
            {
                return 2;
            }
            using var github = GitHubProtocolHost.CreateClient(port, fingerprint);
            return await RunCommand.RunAsync(args[3..], Console.Out, Console.Error,
                context => ActiveControllerRuntime.RunAsync(context, github)).ConfigureAwait(false);
        }

        return args.Any(argument => argument is "-automated" or "@@")
            ? await ConsoleRunner.Run(args).ConfigureAwait(false)
            : await TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions).ConfigureAwait(false);
    }
}
