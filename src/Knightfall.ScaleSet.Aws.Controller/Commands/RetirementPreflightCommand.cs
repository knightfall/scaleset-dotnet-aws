using System.Globalization;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.DynamoDb;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

public static class RetirementPreflightCommand
{
    private const string Usage = "Use preflight retire --current-config <runtime.json> --current-sha256 <hex> " +
        "--current-generation <number> --config <runtime.json> --sha256 <hex> --generation <number>.";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        var options = Parse(args);
        if (options is null)
        {
            await stderr.WriteLineAsync(Usage.AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        try
        {
            var current = await LoadAsync(options.Current, token).ConfigureAwait(false);
            var candidate = await LoadAsync(options.Candidate, token).ConfigureAwait(false);
            if (current.Aws != candidate.Aws || current.EnvironmentName != candidate.EnvironmentName ||
                current.TrackingTable != candidate.TrackingTable)
            {
                await stderr.WriteLineAsync("The candidate must preserve the deployed account, Region, identity and tracking table."
                    .AsMemory(), cancellationToken).ConfigureAwait(false);
                return 1;
            }
            var result = await VerifyAsync(current, options.Current.Generation, candidate, options.Candidate.Generation, token)
                .ConfigureAwait(false);
            if (result.Status == RetirementPreflightStatus.Verified)
            {
                await stdout.WriteLineAsync("Retirement preflight verified.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return 0;
            }
            var profiles = result.Profiles.Count == 0 ? string.Empty : $" ({string.Join(", ", result.Profiles)})";
            await stderr.WriteLineAsync($"Retirement preflight refused the update ({result.Status}){profiles}."
                .AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("Retirement preflight exceeded its read deadline.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }
        catch (Exception error) when (error is AmazonClientException or AmazonServiceException or IOException or
            UnauthorizedAccessException or RuntimeConfigurationException or TimeoutException or ArgumentException)
        {
            await stderr.WriteLineAsync("Retirement preflight could not verify the deployed catalog.".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<RetirementPreflightResult> VerifyAsync(
        RuntimeConfiguration current, long currentGeneration, RuntimeConfiguration candidate, long generation, CancellationToken cancellationToken)
    {
        var region = RegionEndpoint.GetBySystemName(current.Aws.Region);
        var dynamoOptions = new AmazonDynamoDBConfig
        {
            RegionEndpoint = region,
            IgnoreConfiguredEndpointUrls = true,
            AllowAutoRedirect = false,
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
            DisableClockSkewCorrection = true,
        };
        var credentials = await DefaultAWSCredentialsIdentityResolver.GetCredentialsAsync(dynamoOptions).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        using var sts = new AmazonSecurityTokenServiceClient(credentials, new AmazonSecurityTokenServiceConfig
        {
            RegionEndpoint = region,
            IgnoreConfiguredEndpointUrls = true,
            AllowAutoRedirect = false,
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
            DisableClockSkewCorrection = true,
        });
        var caller = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken).ConfigureAwait(false);
        if (caller.Account != current.Aws.Account)
        {
            return new RetirementPreflightResult(RetirementPreflightStatus.BindingMismatch, []);
        }
        using var dynamo = new AmazonDynamoDBClient(credentials, dynamoOptions);
        var store = new DynamoDbLifecycleStore(dynamo, current.TrackingTable.Name, TimeProvider.System);
        return await store.VerifyRetirementAsync(current, currentGeneration, candidate, generation, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RuntimeConfiguration> LoadAsync(Artifact artifact, CancellationToken cancellationToken) =>
        RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(artifact.Path, cancellationToken).ConfigureAwait(false), artifact.Digest);

    private static DeploymentUpdate? Parse(string[] args)
    {
        if (args.Length != 14 || args[0] != "preflight" || args[1] != "retire")
        {
            return null;
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 2; index < args.Length; index += 2)
        {
            string flag = args[index];
            string value = args[index + 1];
            if (flag is not ("--current-config" or "--current-sha256" or "--current-generation" or "--config" or "--sha256" or "--generation") ||
                string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(flag, value))
            {
                return null;
            }
        }
        if (!long.TryParse(values["--current-generation"], NumberStyles.None, CultureInfo.InvariantCulture, out var previous) || previous <= 0 ||
            !long.TryParse(values["--generation"], NumberStyles.None, CultureInfo.InvariantCulture, out var next) || next <= previous)
        {
            return null;
        }
        return new DeploymentUpdate(new Artifact(values["--current-config"], values["--current-sha256"], previous),
            new Artifact(values["--config"], values["--sha256"], next));
    }

    private sealed record Artifact(string Path, string Digest, long Generation);

    private sealed record DeploymentUpdate(Artifact Current, Artifact Candidate);
}
