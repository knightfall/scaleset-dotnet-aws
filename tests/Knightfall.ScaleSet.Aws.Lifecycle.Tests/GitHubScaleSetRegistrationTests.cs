using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SecretsManager;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class GitHubScaleSetRegistrationTests
{
    private const string Group = "No-Docker";
    private const string Set = "linux-shell";
    private const string Intent = "github-scale-set-create";
    private const string Scope = "https://github.com/enterprises/engineering";
    private const string SuccessorTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private const string FixturePat = "ghp_FixtureOnlyNeverValid";
    private const string GroupsPath = GitHubWire.ServicePath + "/_apis/runtime/runnergroups/";
    private const string SetsPath = GitHubWire.ServicePath + "/_apis/runtime/runnerscalesets";
    private static readonly string[] CurrentStage = ["AWSCURRENT"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostCreateResponseUsesExactLookupsAndNeverResendsAcrossRestarts(bool visibleImmediately)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var journal = new DynamoDbEffectJournal(store, clock);
        var registration = new GitHubScaleSetRegistration(store, clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        var fence = new ControllerLeaseRecoveryCoordinator(store, new StoppedObserver(), clock, TimeSpan.FromSeconds(5));
        var configuration = await ConfigurationAsync(cancellation);
        var lease = await AcquireAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        var active = await fence.TryActivateAsync(lease, configuration, Evidence(clock), [], cancellation);
        Assert.Equal(LeaseActivationStatus.Activated, active.Status);
        lease = Assert.IsType<ControllerLease>(active.Lease);
        var profile = new LifecycleProfile(lease.Deployment, Set);
        var ownership = new GitHubRegistrationLease(lease, 41, Evidence(clock));

        bool committedRemotely = false;
        bool showSet = visibleImmediately;
        long groupId = GitHubWire.GroupId;
        using var wire = new GitHubWire(async (request, token) =>
        {
            if (IsCreate(request))
            {
                var durable = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, Intent, token));
                Assert.Equal(EffectState.Executing, durable.Entry.State);
                Assert.Equal(41, durable.Entry.Envelope.Generation);
                Assert.Equal(lease.Epoch, durable.Entry.Attempt?.LeaseEpoch);
                Assert.Null(durable.Entry.Envelope.ProviderReplayDeadline);
                Assert.Equal(clock.Now.AddSeconds(-1), durable.Entry.Envelope.EarliestPossibleFirstSend);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal(groupId, body.RootElement.GetProperty("runnerGroupId").GetInt64());
                Assert.Equal(Set, body.RootElement.GetProperty("name").GetString());
                Assert.Equal([Set, "shell-x64"], body.RootElement.GetProperty("labels").EnumerateArray()
                    .Select(label => label.GetProperty("name").GetString()));
                committedRemotely = true;
                throw new HttpRequestException("fixture transport failed after remote commit " + FixturePat);
            }

            return ReadReply(request, committedRemotely && showSet, groupId);
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        using var remote = await ConnectAsync(configuration, http, clock, cancellation);
        var result = await registration.ResolveAsync(configuration, Set, remote, ownership, null, cancellation);
        var effect = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, Intent, cancellation));
        if (visibleImmediately)
        {
            Assert.Equal(GitHubRegistrationStatus.Bound, result.Status);
            Assert.Equal(GitHubWire.SetId, result.Binding?.ScaleSetId);
            Assert.Equal(EffectResolutionKind.Applied, effect.Entry.Resolution?.Kind);
        }
        else
        {
            Assert.Equal(GitHubRegistrationStatus.RecoveryRequired, result.Status);
            Assert.Equal(EffectState.Uncertain, effect.Entry.State);
            Assert.NotNull(result.Failure);
            Assert.DoesNotContain(FixturePat, result.Failure.ToString(), StringComparison.Ordinal);

            groupId++;
            var drifted = await registration.ResolveAsync(configuration, Set, remote, ownership, null, cancellation);
            Assert.Equal(GitHubRegistrationStatus.RecoveryRequired, drifted.Status);
            groupId--;

            clock.Now = lease.ValidUntil.AddSeconds(2);
            var successor = await AcquireAsync(store, configuration, clock, SuccessorTask, cancellation);
            Assert.Equal(LeaseActivationStatus.ProfileRecoveryPending,
                (await fence.TryActivateAsync(successor, configuration, Evidence(clock), [], cancellation)).Status);
            showSet = true;
            Assert.Equal(LifecycleWriteOutcome.Committed, await fence.ResolveFencedEffectAsync(successor,
                effect, Guid.NewGuid(), Evidence(clock), [], (snapshot, token) => registration.ObserveCreateAsync(remote, snapshot, token), cancellation));
            var activated = await fence.TryActivateAsync(successor, configuration, Evidence(clock), [], cancellation);
            Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
            ownership = new GitHubRegistrationLease(Assert.IsType<ControllerLease>(activated.Lease), 42, Evidence(clock));
        }

        var restarted = new GitHubScaleSetRegistration(store, clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        var recovered = await restarted.ResolveAsync(configuration, Set, remote, ownership, null, cancellation);
        Assert.Equal(GitHubRegistrationStatus.Bound, recovered.Status);
        Assert.Equal(GitHubWire.SetId, recovered.Binding?.ScaleSetId);
        var post = Assert.Single(wire.Requests, IsCreate);
        Assert.Contains(wire.Requests.TakeWhile(request => request != post), IsLookup);
        Assert.Contains(wire.Requests.SkipWhile(request => request != post).Skip(1), IsLookup);
        Assert.DoesNotContain(wire.Requests, request => request.Uri.AbsolutePath.Contains("/sessions", StringComparison.Ordinal));
        Assert.Equal(EffectState.Settled, (await journal.ReadAsync(profile, Intent, cancellation))?.Entry.State);
    }

    [Fact]
    public async Task SendAuthorizationIsOneShotAndAnExpiredAuthorizationMakesNoWireCall()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var journal = new DynamoDbEffectJournal(store, clock);
        var fence = new ControllerLeaseRecoveryCoordinator(store, new StoppedObserver(), clock, TimeSpan.FromSeconds(5));
        var configuration = await ConfigurationAsync(cancellation);
        var acquired = await AcquireAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        var active = await fence.TryActivateAsync(acquired, configuration, Evidence(clock), [], cancellation);
        Assert.Equal(LeaseActivationStatus.Activated, active.Status);
        var lease = Assert.IsType<ControllerLease>(active.Lease);
        var profile = new LifecycleProfile(lease.Deployment, Set);
        var request = new GitHubScaleSetCreateEffectRequest(Scope, Group, GitHubWire.GroupId, Set, [Set, "shell-x64"]);

        using var wire = new GitHubWire((call, _) => Task.FromResult(IsCreate(call)
            ? GitHubWire.Json(HttpStatusCode.OK, ScaleSetJson(GitHubWire.GroupId))
            : ReadReply(call, false, GitHubWire.GroupId)));
        using var http = new HttpClient(wire, disposeHandler: false);
        using var remote = await ConnectAsync(configuration, http, clock, cancellation);

        var allowed = await AuthorizeAsync("one-shot");
        Assert.Equal(GitHubWire.SetId, (await remote.CreateScaleSetAsync(allowed, cancellation)).ScaleSetId);
        var duplicate = await Assert.ThrowsAsync<GitHubOperationException>(() => remote.CreateScaleSetAsync(allowed, cancellation));
        Assert.Equal(GitHubOperationError.AuthorizationConsumed, duplicate.Error);

        var expired = await AuthorizeAsync("expired");
        clock.Now = expired.Attempt.Deadline;
        var denied = await Assert.ThrowsAsync<GitHubOperationException>(() => remote.CreateScaleSetAsync(expired, cancellation));
        Assert.Equal(GitHubOperationError.DeadlineElapsed, denied.Error);
        clock.Now = expired.Attempt.Deadline.AddSeconds(-5);
        var rewound = await Assert.ThrowsAsync<GitHubOperationException>(() => remote.CreateScaleSetAsync(expired, cancellation));
        Assert.Equal(GitHubOperationError.AuthorizationConsumed, rewound.Error);
        Assert.Single(wire.Requests, IsCreate);

        async Task<EffectSendAuthorization> AuthorizeAsync(string intent)
        {
            var envelope = EffectEnvelope.Create(profile, intent, 1, request, clock.Now.AddSeconds(-1), null);
            Assert.Equal(LifecycleWriteOutcome.Committed, await journal.PlanAsync(lease, envelope, Guid.NewGuid(), cancellation));
            var planned = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(profile, intent, cancellation));
            var begun = await journal.BeginAsync(lease, planned, Guid.NewGuid(), Guid.NewGuid(), clock.Now.AddSeconds(1),
                clock.Now.AddSeconds(20), TimeSpan.FromSeconds(5), cancellation);
            return Assert.IsType<EffectSendAuthorization>(begun.Permission);
        }
    }

    [Fact]
    public async Task DryRunAndUnavailableOwnershipAvoidBothDynamoAndGitHubCalls()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var registration = new GitHubScaleSetRegistration(store, clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        var dry = await ConfigurationAsync(cancellation, dryRun: true);
        var active = await ConfigurationAsync(cancellation);

        Assert.Equal(GitHubRegistrationStatus.DryRun, (await registration.ResolveAsync(dry, Set, null, null, null, cancellation)).Status);
        Assert.Equal(GitHubRegistrationStatus.LeaseUnavailable,
            (await registration.ResolveAsync(active, Set, null, null, null, cancellation)).Status);
        Assert.Empty(table.Wire.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingRegistrationUsesExactPinnedLookupOrReportsMissingWithoutWriting(bool pinned)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(cancellation);
        var clock = new ContractClock();
        var store = new DynamoDbLifecycleStore(table.Client, table.Name, clock);
        var registration = new GitHubScaleSetRegistration(store, clock, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        var fence = new ControllerLeaseRecoveryCoordinator(store, new StoppedObserver(), clock, TimeSpan.FromSeconds(5));
        var configuration = await ConfigurationAsync(cancellation, existing: true, pinned: pinned);
        var acquired = await AcquireAsync(store, configuration, clock, MiniStackTable.FirstTask, cancellation);
        var active = await fence.TryActivateAsync(acquired, configuration, Evidence(clock), [], cancellation);
        Assert.Equal(LeaseActivationStatus.Activated, active.Status);
        var ownership = new GitHubRegistrationLease(Assert.IsType<ControllerLease>(active.Lease), 1, Evidence(clock));

        string pinnedPath = SetsPath + "/" + GitHubWire.SetId.ToString(CultureInfo.InvariantCulture);
        using var wire = new GitHubWire((request, _) => Task.FromResult(request.Uri.AbsolutePath == pinnedPath
            ? GitHubWire.Json(HttpStatusCode.OK, ScaleSetJson(GitHubWire.GroupId))
            : ReadReply(request, false, GitHubWire.GroupId)));
        using var http = new HttpClient(wire, disposeHandler: false);
        using var remote = await ConnectAsync(configuration, http, clock, cancellation);
        table.Wire.Clear();

        var result = await registration.ResolveAsync(configuration, Set, remote, ownership, null, cancellation);
        Assert.Equal(pinned ? GitHubRegistrationStatus.Bound : GitHubRegistrationStatus.Missing, result.Status);
        Assert.Equal(pinned ? GitHubWire.SetId : null, result.Binding?.ScaleSetId);
        Assert.Single(wire.Requests, request => request.Method == HttpMethod.Get && request.Uri.AbsolutePath == (pinned ? pinnedPath : SetsPath));
        Assert.DoesNotContain(wire.Requests, IsCreate);
        Assert.DoesNotContain(table.Wire.Requests, request => request.Operation == "TransactWriteItems");
    }

    private static bool IsCreate(GitHubRequest request) => request.Uri.AbsolutePath == SetsPath && request.Method == HttpMethod.Post;

    private static bool IsLookup(GitHubRequest request) => request.Uri.AbsolutePath == SetsPath && request.Method == HttpMethod.Get;

    private static async Task<RuntimeConfiguration> ConfigurationAsync(
        CancellationToken cancellationToken, bool dryRun = false, bool existing = false, bool pinned = false)
    {
        string json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), cancellationToken);
        var root = JsonNode.Parse(json)!;
        var specification = root["spec"]!;
        specification["mode"] = dryRun ? "dry-run" : "active";
        var connection = specification["github"]!["engineering"]!;
        connection["scopeURL"] = Scope;
        connection["auth"]!["type"] = "enterprise-pat";
        var binding = specification["profiles"]![0]!["github"]!;
        if (existing)
        {
            binding["registration"] = "existing";
        }
        if (pinned)
        {
            binding["scaleSetId"] = GitHubWire.SetId.ToString(CultureInfo.InvariantCulture);
        }
        return RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    private static async Task<GitHubScaleSetConnection> ConnectAsync(
        RuntimeConfiguration configuration, HttpClient http, TimeProvider clock, CancellationToken cancellationToken)
    {
        var secretPayload = JsonSerializer.Serialize(new { token = FixturePat });
        var secretResponse = JsonSerializer.Serialize(new
        {
            ARN = configuration.GitHub["engineering"].SecretArn,
            VersionId = "0123456789abcdef0123456789abcdef",
            VersionStages = CurrentStage,
            SecretString = secretPayload,
        });
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(secretResponse));
        using var sdk = new AmazonSecretsManagerClient(AwsWireTransport.Credentials, wire.Configure(new AmazonSecretsManagerConfig()));
        var secret = Assert.IsType<GitHubSecretVersion>(await new GitHubSecretLoader(sdk)
            .LoadAsync(configuration, "engineering", cancellationToken));
        return new GitHubScaleSetClientFactory(httpClient: http, timeProvider: clock).Create(secret);
    }

    private static HttpResponseMessage ReadReply(GitHubRequest request, bool visible, long groupId) => request.Uri.AbsolutePath switch
    {
        GroupsPath => GitHubWire.Json(HttpStatusCode.OK, $$"""{"count":1,"value":[{"id":{{groupId}},"name":"{{Group}}"}]}"""),
        SetsPath when request.Method == HttpMethod.Get => GitHubWire.Json(HttpStatusCode.OK,
            visible ? $$"""{"count":1,"value":[{{ScaleSetJson(groupId)}}]}""" : """{"count":0,"value":[]}"""),
        _ => throw new InvalidOperationException("Unexpected fixture operation."),
    };

    private static string ScaleSetJson(long groupId) =>
        $$"""{"id":{{GitHubWire.SetId}},"runnerGroupId":{{groupId}},"name":"{{Set}}","labels":[{"name":"{{Set}}","type":"System"},{"name":"shell-x64","type":"System"}]}""";

    private static async Task<ControllerLease> AcquireAsync(DynamoDbLifecycleStore store, RuntimeConfiguration configuration,
        ContractClock clock, string task, CancellationToken cancellationToken)
    {
        var attempt = await store.TryAcquireLeaseAsync(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration), task, task,
            ControllerLease.CurrentWriterProtocol, clock.Now.AddSeconds(-1), clock.Now.AddMinutes(2), cancellationToken);
        Assert.Equal(LifecycleWriteOutcome.Committed, attempt.Outcome);
        return Assert.IsType<ControllerLease>(attempt.Lease);
    }

    private static ControllerClockEvidence Evidence(ContractClock clock) =>
        new(clock.Now, clock.Now.AddMinutes(1), TimeSpan.FromSeconds(1));

    private sealed class StoppedObserver : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(MiniStackTable.FirstTask, taskArn);
            return Task.FromResult(ControllerTaskFenceStatus.Stopped);
        }
    }
}
