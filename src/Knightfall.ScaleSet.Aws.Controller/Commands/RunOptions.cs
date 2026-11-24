using System.Globalization;
using Knightfall.ScaleSet.Aws.Controller.Runtime;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

internal sealed record RunOptions(
    string? ConfigPath, string? S3Uri, string? VersionId, string Sha256, long Generation,
    string? HealthAddress, string? OtlpEndpoint, string? Ec2Queue, string? EcsQueue)
{
    internal static RunOptions? Parse(string[] args)
    {
        if (args.Length % 2 == 0 || args[0] != "run")
        {
            return null;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            string name = args[index];
            string value = args[index + 1];
            if (name is not ("--config" or "--config-s3-uri" or "--config-version-id" or "--sha256" or "--generation" or
                "--health-addr" or "--otlp-endpoint" or "--ec2-event-queue-url" or "--ecs-event-queue-url") ||
                !ConfigVerifyCommand.HasValue(value) || !options.TryAdd(name, value))
            {
                return null;
            }
        }

        string? configPath = options.GetValueOrDefault("--config");
        string? s3Uri = options.GetValueOrDefault("--config-s3-uri");
        string? versionId = options.GetValueOrDefault("--config-version-id");
        bool local = configPath is not null && s3Uri is null && versionId is null;
        bool remote = configPath is null && s3Uri is not null && versionId is not null;
        if ((!local && !remote) || !options.TryGetValue("--sha256", out string? sha256) ||
            !long.TryParse(options.GetValueOrDefault("--generation"), NumberStyles.None, CultureInfo.InvariantCulture, out long generation) ||
            generation <= 0)
        {
            return null;
        }

        return new RunOptions(configPath, s3Uri, versionId, sha256, generation, options.GetValueOrDefault("--health-addr"),
            options.GetValueOrDefault("--otlp-endpoint"), options.GetValueOrDefault("--ec2-event-queue-url"),
            options.GetValueOrDefault("--ecs-event-queue-url"));
    }

    internal ControllerEventQueues BindQueues(RuntimeConfiguration configuration, Uri? loopback)
    {
        string? ec2 = BindQueue(Ec2Queue, "--ec2-event-queue-url", RunnerProvisionerType.Ec2, configuration, loopback);
        string? ecs = BindQueue(EcsQueue, "--ecs-event-queue-url", RunnerProvisionerType.Fargate, configuration, loopback);
        if (ec2 is not null && ec2 == ecs)
        {
            throw new ArgumentException("The EC2 and ECS event queues must be distinct.");
        }
        return new ControllerEventQueues(ec2, ecs);
    }

    private static string? BindQueue(
        string? value, string flag, RunnerProvisionerType provisioner, RuntimeConfiguration configuration, Uri? loopback)
    {
        if (value is null)
        {
            return null;
        }
        if (configuration.Mode != ControllerMode.Active || !configuration.Profiles.Any(profile =>
            profile.State != RunnerProfileState.Disabled && profile.Provisioner.Type == provisioner))
        {
            throw new ArgumentException($"The {flag} option requires an enabled runner backend in active mode.");
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString() ||
            uri.UserInfo.Length != 0 || value.Contains('?') || value.Contains('#') || uri.AbsolutePath.Length <= 1 ||
            !(loopback is null ? MatchesDeploymentQueue(uri, configuration.Aws) :
                uri.GetLeftPart(UriPartial.Authority) == loopback.GetLeftPart(UriPartial.Authority)))
        {
            throw new ArgumentException($"The {flag} option must address the verified deployment's SQS endpoint.");
        }
        return uri.AbsoluteUri;
    }

    private static bool MatchesDeploymentQueue(Uri uri, AwsEnvironment aws)
    {
        string suffix = aws.Region.StartsWith("cn-", StringComparison.Ordinal) ? "amazonaws.com.cn" : "amazonaws.com";
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.IdnHost != $"sqs.{aws.Region}.{suffix}")
        {
            return false;
        }

        string[] path = uri.AbsolutePath[1..].Split('/');
        if (path.Length != 2 || path[0] != aws.Account || path[1].Length > 80)
        {
            return false;
        }
        string name = path[1];
        if (name.EndsWith(".fifo", StringComparison.Ordinal))
        {
            name = name[..^5];
        }
        return name.Length != 0 && name.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}
