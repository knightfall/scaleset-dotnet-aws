using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.IdentityManagement;
using Knightfall.ScaleSet.Aws.Controller.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.GitHub;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;
using Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

public sealed class GitHubJitControllerTests
{
    private const string EncodedJit = "synthetic-jit-controller-wire-probe";
    private const string SecondTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000002";
    private const string RunnersPath = GitHubWire.ServicePath + "/_apis/distributedtask/pools/0/agents";
    private const long RunnerId = GitHubWire.RunnerId;
    private static readonly GitHubJitTiming Timing = new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20),
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerVerifiesTaskRolePublishesOnlyCiphertextAndRemovesOnlyAfterResourceDeath(bool schedulingConflict)
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var configuration = await ControllerLifecycleTests.ConfigurationAsync(draining: false, token);
        var fixture = await JitHandoffStoreTests.CreateFixtureAsync(table, token, configuration);
        var runnerName = "ss-" + JitOfferBinding.GetOfferId(fixture.Identity)[..48];
        string runnerJson = RunnerJson(fixture.Identity.ScaleSetId, runnerName);
        bool removedElsewhere = false;
        using var wire = new GitHubWire((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(JitPath(fixture.Identity.ScaleSetId), request.Uri.AbsolutePath);
                AssertJitCommand(request, runnerName);
                return GitHubWire.Reply(HttpStatusCode.OK, $$"""{"runner":{{runnerJson}},"encodedJITConfig":"{{EncodedJit}}"}""");
            }
            Assert.Equal(RunnersPath + "/" + RunnerId, request.Uri.AbsolutePath);
            if (request.Method == HttpMethod.Get)
            {
                return removedElsewhere
                    ? GitHubWire.Reply(HttpStatusCode.NotFound, """{"typeName":"Microsoft.VisualStudio.Services.AgentPool.AgentNotFoundException"}""")
                    : GitHubWire.Reply(HttpStatusCode.OK, runnerJson);
            }
            return schedulingConflict
                ? GitHubWire.Reply(HttpStatusCode.Conflict, """{"typeName":"Microsoft.VisualStudio.Services.AgentPool.JobStillRunningException"}""")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        using var connection = await ControllerLifecycleTests.ConnectAsync(configuration, fixture.Clock, http, token);
        using var iamWire = RoleWire(fixture.Catalog.Binding.BootstrapRoleArn, fixture.Identity.AwsUserId.Split(':')[0]);
        using var iam = new AmazonIdentityManagementServiceClient(AwsWireTransport.Credentials, iamWire.Configure(new AmazonIdentityManagementServiceConfig()));
        using var ownership = new ControllerLeaseOwnership(fixture.Store, fixture.Lease, Proof(fixture.Clock), fixture.Clock,
            TimeSpan.FromMinutes(5), Timing.WriteReserve);
        using var recipient = JitRecipientKey.Create();
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Bootstrap.RegisterHelloAsync(recipient.Hello, token));
        var service = new GitHubJitHandoff(fixture.Store, fixture.Clock, Timing);
        var coordinator = new ControllerJitCoordinator(fixture.Store, new AwsBootstrapPrincipalResolver(iam), service);
        var observation = new BootstrapProviderObservation(fixture.Executor.Binding, fixture.Catalog.Binding.BootstrapRoleArn, fixture.Clock.Now);
        Assert.Equal(GitHubJitStatus.Held,
            (await coordinator.AdvanceAsync(configuration, ownership, observation with { CredentialSourceArn = observation.CredentialSourceArn + "-other" },
                connection, token)).Status);
        Assert.Empty(iamWire.Requests);
        Assert.Empty(wire.Requests);
        var published = await coordinator.AdvanceAsync(configuration, ownership, observation, connection, token);
        Assert.Equal(new GitHubJitResult(GitHubJitStatus.Offered, RunnerId), published);
        var iamRequest = Assert.Single(iamWire.Requests);
        Assert.Contains("Action=GetRole", iamRequest.Body, StringComparison.Ordinal);
        Assert.Contains("RoleName=" + observation.CredentialSourceArn.Split('/')[^1], iamRequest.Body, StringComparison.Ordinal);
        Assert.Single(ServiceRequests(wire), request => request.Method == HttpMethod.Post);
        Assert.DoesNotContain(table.Wire.Requests, request => request.Request.GetRawText().Contains(EncodedJit, StringComparison.Ordinal));

        var authority = Assert.IsType<ActiveControllerAuthority>(ownership.ReadAuthority());
        Assert.Equal(LifecycleWriteOutcome.Conflict, await service.RemoveTerminatedRunnerAsync(authority, fixture.Catalog.Profile,
            fixture.Identity.AllocationId, connection, ownership.OwnershipLost, token));
        Assert.DoesNotContain(ServiceRequests(wire), request => request.Method == HttpMethod.Delete);
        var receipt = await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, token);
        Assert.Equal(BootstrapOfferStatus.Consumed, receipt.Status);
        using var plaintext = recipient.Open(Assert.IsType<JitOfferEnvelope>(receipt.Offer), fixture.Identity, fixture.Clock);
        Assert.Equal(EncodedJit, Encoding.UTF8.GetString(plaintext.Bytes));

        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Store.ObserveResourceAsync(fixture.Lease.Fence, fixture.Executor.Binding,
            new ExecutorResourceObservation(ExecutorResourceState.IrreversiblyTerminated, fixture.Clock.Now, fixture.Executor.State.StartedAt,
                Guid.NewGuid()), token));
        Task<LifecycleWriteOutcome> RemoveAsync() => service.RemoveTerminatedRunnerAsync(authority, fixture.Catalog.Profile,
            fixture.Identity.AllocationId, connection, ownership.OwnershipLost, token);
        var cleanup = await RemoveAsync();
        if (schedulingConflict)
        {
            Assert.Equal(LifecycleWriteOutcome.Indeterminate, cleanup);
            Assert.Equal(1, (await fixture.Store.ReadProfileHandoffObservationAsync(fixture.Catalog.Profile, token)).OutstandingOffers);
            removedElsewhere = true;
            Assert.Equal(LifecycleWriteOutcome.Indeterminate, await RemoveAsync());
            Assert.Single(ServiceRequests(wire), request => request.Method == HttpMethod.Get);
            fixture.Clock.Now += Timing.SendWindow + TimeSpan.FromSeconds(3);
            cleanup = await RemoveAsync();
            Assert.Equal(2, ServiceRequests(wire).Count(request => request.Method == HttpMethod.Get));
        }
        Assert.Equal(LifecycleWriteOutcome.Committed, cleanup);
        Assert.Single(ServiceRequests(wire), request => request.Method == HttpMethod.Delete && request.Uri.AbsolutePath == RunnersPath + "/" + RunnerId);
        Assert.Equal(0, (await fixture.Store.ReadProfileHandoffObservationAsync(fixture.Catalog.Profile, token)).OutstandingOffers);
    }

    [Fact]
    public async Task DroppedGenerateResponseIsOneWireAttemptAndFencedLookupNeverOffersOrRemints()
    {
        var token = TestContext.Current.CancellationToken;
        using var table = await MiniStackTable.CreateAsync(token);
        var configuration = await ControllerLifecycleTests.ConfigurationAsync(draining: false, token);
        var fixture = await JitHandoffStoreTests.CreateFixtureAsync(table, token, configuration);
        using var recipient = JitRecipientKey.Create();
        Assert.Equal(LifecycleWriteOutcome.Committed, await fixture.Bootstrap.RegisterHelloAsync(recipient.Hello, token));
        string offerId = JitOfferBinding.GetOfferId(fixture.Identity);
        string runnerName = "ss-" + offerId[..48];
        string runnerJson = RunnerJson(fixture.Identity.ScaleSetId, runnerName);
        string intentId = "jit-" + offerId;
        using var wire = new GitHubWire(async (request, cancellation) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(JitPath(fixture.Identity.ScaleSetId), request.Uri.AbsolutePath);
                AssertJitCommand(request, runnerName);
                var executing = await fixture.Journal.ReadAsync(fixture.Catalog.Profile, intentId, cancellation);
                Assert.Equal(EffectState.Executing, executing?.Entry.State);
                Assert.Null(executing?.Entry.Envelope.ProviderReplayDeadline);
                throw new HttpRequestException(EncodedJit);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.Uri.AbsolutePath == RunnersPath)
            {
                Assert.Contains("agentName=" + runnerName, request.Uri.Query[1..].Split('&'));
                return GitHubWire.Json(HttpStatusCode.OK, $$"""{"count":1,"value":[{{runnerJson}}]}""");
            }
            Assert.Equal(RunnersPath + "/" + RunnerId, request.Uri.AbsolutePath);
            return GitHubWire.Json(HttpStatusCode.OK, runnerJson);
        });
        using var http = new HttpClient(wire, disposeHandler: false);
        using var connection = await ControllerLifecycleTests.ConnectAsync(configuration, fixture.Clock, http, token);
        var service = new GitHubJitHandoff(fixture.Store, fixture.Clock, Timing);
        var issued = await service.IssueAsync(new ActiveControllerAuthority(fixture.Lease, Proof(fixture.Clock)), fixture.Identity, fixture.Clock.Now,
            connection, CancellationToken.None, token);
        Assert.Equal(GitHubJitStatus.RecoveryRequired, issued.Status);
        Assert.DoesNotContain(EncodedJit, Assert.IsType<GitHubOperationException>(issued.Failure).ToString(), StringComparison.Ordinal);
        Assert.Single(ServiceRequests(wire));
        var pending = Assert.IsType<EffectJournalSnapshot>(await fixture.Journal.ReadAsync(fixture.Catalog.Profile, intentId, token));
        Assert.Equal(EffectState.Uncertain, pending.Entry.State);

        fixture.Clock.Now = fixture.Lease.ValidUntil.AddSeconds(2);
        var acquired = await fixture.Store.TryAcquireLeaseAsync(fixture.Lease.Deployment, "recovered-jit", SecondTask,
            ControllerLease.CurrentWriterProtocol, fixture.Clock.Now.AddSeconds(-1), fixture.Clock.Now.AddMinutes(5), token);
        var successor = Assert.IsType<ControllerLease>(acquired.Lease);
        var fencing = new ControllerLeaseRecoveryCoordinator(fixture.Store, new StoppedTask(), fixture.Clock, Timing.WriteReserve);
        var proof = Proof(fixture.Clock);
        var blocked = await fencing.TryActivateAsync(successor, configuration, proof, [], token);
        Assert.NotEqual(LeaseActivationStatus.Activated, blocked.Status);
        var restarted = new GitHubJitHandoff(fixture.Store, fixture.Clock, Timing);
        var outcome = await restarted.RecoverFencedAsync(successor, proof, pending,
            new EffectRecoveryAuthority(fencing, []), connection, token);
        var marker = await fixture.Store.ReadJitHandoffAsync(fixture.Catalog.Profile, fixture.Identity.AllocationId, token);
        Assert.True(outcome == LifecycleWriteOutcome.Committed,
            $"Recovery returned {outcome} with {marker?.State} evidence after {string.Join(", ", ServiceRequests(wire).Select(r => r.Uri.PathAndQuery))}.");
        var active = await fencing.TryActivateAsync(successor, configuration, proof, [], token);
        Assert.Equal(LeaseActivationStatus.Activated, active.Status);
        var recovered = await restarted.IssueAsync(new ActiveControllerAuthority(Assert.IsType<ControllerLease>(active.Lease), proof),
            fixture.Identity, fixture.Clock.Now, connection, CancellationToken.None, token);
        Assert.Equal(new GitHubJitResult(GitHubJitStatus.RegisteredWithoutOffer, RunnerId), recovered);
        Assert.Equal(BootstrapOfferStatus.NotAvailable, (await fixture.Bootstrap.TryConsumeOfferAsync(recipient.Hello, token)).Status);
        Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Get, HttpMethod.Get }, ServiceRequests(wire).Select(request => request.Method));
        Assert.DoesNotContain(table.Wire.Requests, request => request.Request.GetRawText().Contains(EncodedJit, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ec2IdentityRequiresTheSameRoleIdFromInstanceProfileAndRole(bool matching)
    {
        const string RoleId = "AROAFIXTUREHANDOFF002";
        const string InstanceId = "i-00000000000000001";
        var token = TestContext.Current.CancellationToken;
        var document = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "ec2-only-pinned.json"), token))!;
        var dryRun = RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(document));
        document["spec"]!["mode"] = "active";
        var configuration = RuntimeConfigurationLoader.Load(JsonSerializer.SerializeToUtf8Bytes(document));
        var configured = Assert.Single(configuration.Profiles);
        var ec2 = Assert.IsType<Ec2ProvisionerConfiguration>(configured.Provisioner);
        string scope = GitHubRoutingIdentity.Scope(configuration.GitHub[configured.GitHub.Connection].ScopeUrl);
        var profile = new LifecycleProfile(ControllerLeaseRecoveryCoordinator.DeploymentKey(configuration), configured.Id);
        var catalog = new CatalogProfile(profile, new CatalogBinding(scope, configured.GitHub.RunnerGroup, configured.GitHub.ScaleSet,
            configured.GitHub.Labels, RunnerProvisionerType.Ec2, ec2.LaunchTemplateId, ec2.BootstrapRoleArn,
            ec2.BootstrapProtocol, 42, 84), CatalogProfileState.Active, 1, 1, Guid.NewGuid(), null, null);
        string instanceArn = $"arn:aws:ec2:{configuration.Aws.Region}:{configuration.Aws.Account}:instance/{InstanceId}";
        var binding = new ExecutorBinding(profile, Guid.NewGuid().ToString("D"), instanceArn, Guid.NewGuid(),
            new Ec2ExecutorIdentity(MiniStackTable.Now.AddMinutes(-1), ec2.LaunchTemplateId, 3));
        var observation = new BootstrapProviderObservation(binding, ec2.InstanceProfileArn, MiniStackTable.Now);
        using var wire = new AwsWireTransport(request => request.Body!.Contains("Action=GetInstanceProfile", StringComparison.Ordinal)
            ? AwsWireTransport.Xml($$"""
            <GetInstanceProfileResponse xmlns="https://iam.amazonaws.com/doc/2010-05-08/">
              <GetInstanceProfileResult><InstanceProfile><Arn>{{ec2.InstanceProfileArn}}</Arn>
                <Roles><member><Arn>{{ec2.BootstrapRoleArn}}</Arn><RoleId>{{RoleId}}</RoleId></member></Roles>
              </InstanceProfile></GetInstanceProfileResult>
            </GetInstanceProfileResponse>
            """)
            : AwsWireTransport.Xml($$"""
            <GetRoleResponse xmlns="https://iam.amazonaws.com/doc/2010-05-08/">
              <GetRoleResult><Role><Arn>{{ec2.BootstrapRoleArn}}</Arn>
              <RoleId>{{(matching ? RoleId : "AROAFIXTURERECREATED2")}}</RoleId></Role></GetRoleResult>
            </GetRoleResponse>
            """));
        using var iam = new AmazonIdentityManagementServiceClient(AwsWireTransport.Credentials, wire.Configure(new AmazonIdentityManagementServiceConfig()));
        var resolver = new AwsBootstrapPrincipalResolver(iam);
        Assert.Null(await resolver.ResolveAsync(dryRun, catalog, observation, token));
        Assert.Empty(wire.Requests);
        var identity = await resolver.ResolveAsync(configuration, catalog, observation, token);
        Assert.Collection(wire.Requests,
            request => Assert.Contains("InstanceProfileName=" + ec2.InstanceProfileArn.Split('/')[^1], request.Body, StringComparison.Ordinal),
            request => Assert.Contains("RoleName=" + ec2.BootstrapRoleArn.Split('/')[^1], request.Body, StringComparison.Ordinal));
        if (matching)
        {
            Assert.Equal(RoleId + ":" + InstanceId, Assert.IsType<JitHandoffIdentity>(identity).AwsUserId);
        }
        else
        {
            Assert.Null(identity);
        }
    }

    private static string JitPath(long scaleSetId) => $"{GitHubWire.ServicePath}/_apis/runtime/runnerscalesets/{scaleSetId}/generatejitconfig";

    private static string RunnerJson(long scaleSetId, string name) => $$"""{"id":{{RunnerId}},"runnerScaleSetId":{{scaleSetId}},"name":"{{name}}"}""";

    private static GitHubRequest[] ServiceRequests(GitHubWire wire) =>
        wire.Requests.Where(request => request.Uri.Host == GitHubWire.ServiceHost).ToArray();

    private static void AssertJitCommand(GitHubRequest request, string runnerName)
    {
        using var payload = JsonDocument.Parse(request.Body!);
        Assert.Equal(runnerName, payload.RootElement.GetProperty("name").GetString());
        Assert.Equal("_work", payload.RootElement.GetProperty("workFolder").GetString());
    }

    private static AwsWireTransport RoleWire(string roleArn, string roleId) => new(_ => AwsWireTransport.Xml($$"""
        <GetRoleResponse xmlns="https://iam.amazonaws.com/doc/2010-05-08/">
          <GetRoleResult><Role><Arn>{{roleArn}}</Arn><RoleId>{{roleId}}</RoleId></Role></GetRoleResult>
        </GetRoleResponse>
        """));

    private static ControllerClockEvidence Proof(ContractClock clock) => new(clock.Now, clock.Now.AddMinutes(3), TimeSpan.FromSeconds(1));

    private sealed class StoppedTask : IControllerTaskFenceObserver
    {
        public Task<ControllerTaskFenceStatus> ReadAsync(string taskArn, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(MiniStackTable.FirstTask, taskArn);
            return Task.FromResult(ControllerTaskFenceStatus.Stopped);
        }
    }
}
