using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using ScaleSet.Listener;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class GitHubLifecycleIntegrationTests
{
    private const string Region = "ap-southeast-2";
    private const string ProfileId = "linux-shell";
    private const string ConnectionId = "engineering";
    private const string Group = "No-Docker";
    private static readonly TimeSpan WriteReserve = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendWindow = TimeSpan.FromSeconds(30);
    private static readonly GitHubListenerTiming ListenerTiming = new(WriteReserve, SendWindow, TimeSpan.FromSeconds(15));

    [Theory]
    [InlineData("https://github.com/engineering", "/orgs/engineering/actions/runners/registration-token", true)]
    [InlineData("https://example.ghe.com/engineering/tools", "/repos/engineering/tools/actions/runners/registration-token", true)]
    [InlineData("https://github.com/enterprises/engineering", "/enterprises/engineering/actions/runners/registration-token", false)]
    public async Task ScopeCredentialAndDroppedRegistrationRecoverThroughOneRemoteCreate(string scope, string registrationPath, bool app)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        using var secrets = CreateSecretsClient();
        var secret = await StoreSecretAsync(secrets, app, token);
        var configuration = await LoadConfigurationAsync(secret.ARN, scope, app, token);
        var clock = new IntegrationClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var lease = await StartOwnerAsync(store, configuration, clock, token);
        var simulator = new GitHubSimulator(Group, scope.Contains(".ghe.com", StringComparison.Ordinal)
            ? GitHubSimulatorAddress.ForProfile("example") : null);
        await using var host = await GitHubProtocolHost.StartAsync(simulator, token);
        using var http = host.CreateClient();
        var factory = new GitHubScaleSetClientFactory(http, timeProvider: clock);
        var loader = new GitHubSecretLoader(secrets);
        using var remote = await ConnectAsync(configuration, loader, factory, token);
        simulator.FailNext(SimulatorOperation.CreateScaleSet, SimulatorFault.DropAfterCommit);
        var registration = new GitHubScaleSetRegistration(store, clock, WriteReserve, SendWindow);
        var authorization = new GitHubRegistrationLease(lease, 1, ClockEvidence(clock));
        var first = await registration.ResolveAsync(configuration, ProfileId, remote, authorization, null, token);
        Assert.Equal(GitHubRegistrationStatus.Bound, first.Status);
        Assert.Equal(GitHubSimulator.SetId, first.Binding?.ScaleSetId);

        using var freshClient = await ConnectAsync(configuration, loader, factory, token);
        var restarted = new GitHubScaleSetRegistration(store, clock, WriteReserve, SendWindow);
        var confirmed = await restarted.ResolveAsync(configuration, ProfileId, freshClient, authorization, first.Binding, token);
        Assert.Equal(GitHubRegistrationStatus.Bound, confirmed.Status);
        Assert.Equal(GitHubSimulator.SetId, confirmed.Binding?.ScaleSetId);
        var requests = simulator.Requests;
        var post = Assert.Single(requests, request => request.Method == "POST" && request.Path == GitHubSimulator.Sets);
        Assert.Contains(requests.SkipWhile(request => request != post).Skip(1), request =>
            request.Method == "GET" && request.Path == GitHubSimulator.Sets);
        Assert.Contains(requests, request => request.Path == registrationPath && request.Host ==
            (scope.Contains(".ghe.com", StringComparison.Ordinal) ? GitHubProtocolHost.EnterpriseApi : GitHubProtocolHost.PublicApi));
        Assert.Equal(app, requests.Any(request => request.Path.StartsWith("/app/installations/", StringComparison.Ordinal)));
        Assert.DoesNotContain(requests, request => request.Path == GitHubSimulator.Sessions);
        var journal = new DynamoDbEffectJournal(store, clock);
        var effect = await journal.ReadAsync(new LifecycleProfile(lease.Deployment, ProfileId), "github-scale-set-create", token);
        Assert.Equal(EffectResolutionKind.Applied, effect?.Entry.Resolution?.Kind);
    }

    [Fact]
    public async Task AwscurrentValidatesCandidateAndRetiresTheOnlySessionBeforeItsReplacement()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        using var secrets = CreateSecretsClient();
        var secret = await StoreSecretAsync(secrets, app: false, token);
        var configuration = await LoadConfigurationAsync(secret.ARN, "https://github.com/enterprises/engineering", app: false, token);
        var clock = new IntegrationClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var lease = await StartOwnerAsync(store, configuration, clock, token);
        var simulator = new GitHubSimulator(Group);
        await using var host = await GitHubProtocolHost.StartAsync(simulator, token);
        using var http = host.CreateClient();
        var factory = new GitHubScaleSetClientFactory(http, timeProvider: clock);
        var loader = new GitHubSecretLoader(secrets);
        var remote = await ConnectAsync(configuration, loader, factory, token);
        var binding = await RegisterAsync(store, clock, configuration, lease, remote, token);
        var rotator = new GitHubCredentialRotator(loader, factory);
        using var lane = new GitHubListenerSupervisor(store, clock, ListenerTiming, rotator)
            .Register(Definition(configuration, lease, clock, binding, remote, capacity: 2));
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        var original = Assert.IsType<Guid>(simulator.SessionId);
        var switched = await secrets.PutSecretValueAsync(new PutSecretValueRequest
        {
            SecretId = secret.ARN,
            ClientRequestToken = Guid.NewGuid().ToString("N"),
            SecretString = JsonSerializer.Serialize(new { token = GitHubSimulator.NextPat }),
            VersionStages = ["AWSCURRENT"],
        }, token);
        Assert.NotEqual(secret.VersionId, switched.VersionId);
        Assert.Equal(GitHubListenerLaneStatus.CredentialAdopted, (await lane.RotateAsync(token)).Status);
        Assert.Null(simulator.SessionId);
        Assert.Single(simulator.Requests, IsSessionPost);
        Assert.Equal(ScaleSetListenerStepStatus.Initialized, (await lane.StepAsync(token)).Step?.Status);
        Assert.NotEqual(original, simulator.SessionId);
        var oldSessionPath = $"{GitHubSimulator.Sessions}/{original:D}";
        var sessionCalls = simulator.Requests.Where(request => request.Path.StartsWith(GitHubSimulator.Sessions, StringComparison.Ordinal))
            .Select(request => (request.Method, request.Path)).ToArray();
        Assert.Equal([("POST", GitHubSimulator.Sessions), ("DELETE", oldSessionPath), ("POST", GitHubSimulator.Sessions)], sessionCalls);
    }

    private static AmazonSecretsManagerClient CreateSecretsClient()
    {
        string? endpoint = Environment.GetEnvironmentVariable("AWS_ENDPOINT_URL");
        string? tableEndpoint = Environment.GetEnvironmentVariable("SCALESET_MINISTACK_ENDPOINT");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Host != "127.0.0.1" ||
            uri.Scheme != Uri.UriSchemeHttp || uri.IsDefaultPort || uri.AbsolutePath != "/" ||
            !Uri.TryCreate(tableEndpoint, UriKind.Absolute, out var tableUri) || uri != tableUri)
        {
            throw new InvalidOperationException("Run make check-integration to provide the pinned loopback AWS endpoint.");
        }
        return new AmazonSecretsManagerClient(new BasicAWSCredentials("000000000000", "test-only-secret"), new AmazonSecretsManagerConfig
        {
            ServiceURL = uri.AbsoluteUri,
            AuthenticationRegion = Region,
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
            AllowAutoRedirect = false,
            DisableClockSkewCorrection = true,
        });
    }

    private static async Task<CreateSecretResponse> StoreSecretAsync(
        AmazonSecretsManagerClient secrets, bool app, CancellationToken cancellationToken)
    {
        string payload;
        if (app)
        {
            using var key = RSA.Create(2048);
            payload = JsonSerializer.Serialize(new { clientId = "Iv1.fixture", installationId = 456, privateKey = key.ExportPkcs8PrivateKeyPem() });
        }
        else
        {
            payload = JsonSerializer.Serialize(new { token = GitHubSimulator.FirstPat });
        }
        return await secrets.CreateSecretAsync(new CreateSecretRequest
        {
            Name = "scaleset-simulator-" + Guid.NewGuid().ToString("N"),
            ClientRequestToken = Guid.NewGuid().ToString("N"),
            SecretString = payload,
        }, cancellationToken);
    }

    private static async Task<RuntimeConfiguration> LoadConfigurationAsync(
        string secretArn, string scope, bool app, CancellationToken cancellationToken)
    {
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken);
        string account = secretArn.Split(':')[4];
        var profile = JsonNode.Parse(fixture.Replace("111122223333", account, StringComparison.Ordinal))!;
        var connection = profile["spec"]!["github"]![ConnectionId]!;
        connection["scopeURL"] = scope;
        connection["auth"]!["type"] = app ? "github-app" : "enterprise-pat";
        connection["auth"]!["secretArn"] = secretArn;
        return RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(profile));
    }

    private static async Task<GitHubScaleSetConnection> ConnectAsync(
        RuntimeConfiguration configuration, GitHubSecretLoader loader, GitHubScaleSetClientFactory factory, CancellationToken cancellationToken) =>
        factory.Create(Assert.IsType<GitHubSecretVersion>(await loader.LoadAsync(configuration, ConnectionId, cancellationToken)));

    private static async Task<ControllerLease> StartOwnerAsync(
        DynamoDbLifecycleStore store, RuntimeConfiguration configuration, IntegrationClock clock, CancellationToken cancellationToken,
        string ownerTask = MiniStackTable.FirstTask)
    {
        var acquired = await store.TryAcquireLeaseAsync(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration), ownerTask, ownerTask,
            ControllerLease.CurrentWriterProtocol, clock.Now.AddSeconds(-1), clock.Now.AddMinutes(5), cancellationToken);
        var coordinator = new ControllerLeaseRecoveryCoordinator(store, new StoppedController(), clock, WriteReserve);
        var activated = await coordinator.TryActivateAsync(Assert.IsType<ControllerLease>(acquired.Lease), configuration, ClockEvidence(clock),
            [], cancellationToken);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        var lease = Assert.IsType<ControllerLease>(activated.Lease);
        for (int pass = 0; pass < 10; pass++)
        {
            var rollout = await store.AdvanceCatalogRolloutAsync(lease.Fence, configuration, 1, cancellationToken);
            if (rollout.Status == CatalogRolloutStatus.Applied)
            {
                return lease;
            }
            Assert.Equal(CatalogRolloutStatus.Staging, rollout.Status);
        }
        throw new InvalidOperationException("The fixture catalog did not publish.");
    }

    private static async Task<GitHubScaleSetBinding> RegisterAsync(DynamoDbLifecycleStore store, IntegrationClock clock,
        RuntimeConfiguration configuration, ControllerLease lease, GitHubScaleSetConnection remote, CancellationToken cancellationToken)
    {
        var result = await new GitHubScaleSetRegistration(store, clock, WriteReserve, SendWindow).ResolveAsync(configuration, ProfileId,
            remote, new GitHubRegistrationLease(lease, 1, ClockEvidence(clock)), null, cancellationToken);
        var binding = Assert.IsType<GitHubScaleSetBinding>(result.Binding);
        var profile = new LifecycleProfile(lease.Deployment, ProfileId);
        var row = Assert.IsType<CatalogProfile>(await store.ReadCatalogProfileAsync(profile, cancellationToken));
        Assert.Equal(LifecycleWriteOutcome.Committed,
            await store.BindCatalogScaleSetAsync(lease.Fence, row, binding.RunnerGroupId, binding.ScaleSetId, cancellationToken));
        return binding;
    }

    private static GitHubListenerProfile Definition(RuntimeConfiguration configuration, ControllerLease lease, IntegrationClock clock,
        GitHubScaleSetBinding binding, GitHubScaleSetConnection remote, int capacity) => new(configuration, ProfileId, binding, remote, lease.Fence,
        _ => Task.FromResult<GitHubRegistrationLease?>(new GitHubRegistrationLease(lease, 1, ClockEvidence(clock))),
        _ => Task.FromResult(new ListenerCapacity(capacity, 0)), (_, _) => Task.FromResult(true), CancellationToken.None);

    private static bool IsSessionPost(SimulatorRequest request) => request.Method == "POST" && request.Path == GitHubSimulator.Sessions;

    private static ControllerClockEvidence ClockEvidence(IntegrationClock clock) =>
        new(clock.Now, clock.Now.AddMinutes(3), TimeSpan.FromSeconds(1));

    private sealed class IntegrationClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = MiniStackTable.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StoppedController : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken) =>
            Task.FromResult(ControllerTaskFenceStatus.Stopped);
    }
}
