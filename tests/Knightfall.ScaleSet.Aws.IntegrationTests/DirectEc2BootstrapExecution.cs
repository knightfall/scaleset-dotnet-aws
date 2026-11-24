using System.Text;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Ec2;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using Xunit;
using Ec2Bootstrap = Knightfall.ScaleSet.Aws.RunnerBootstrap.RunnerBootstrap;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

/// <summary>Runs the real EC2 identity and handoff code around one controlled job process.</summary>
internal sealed class DirectEc2BootstrapExecution(
    DirectEc2ImdsHost host, HttpClient metadataHttp, DirectEc2BootstrapExecution.OfferGate gate, AmazonDynamoDBClient receipt,
    CancellationTokenSource lifetime, DirectEc2BootstrapExecution.SimulatedJob job, VerifiedEc2BootstrapIdentity identity,
    Task<RunnerBootstrapOutcome> outcome)
    : IAsyncDisposable
{
    internal (string UserId, string ResourceArn) Identity => (identity.AwsUserId, identity.ResourceArn);

    internal Task<RunnerBootstrapOutcome> Outcome => outcome;

    internal DirectEc2ImdsHost Imds => host;

    internal static async Task<DirectEc2BootstrapExecution> StartAsync(MiniStackIamFixture cloud, DirectEc2Fixture ec2,
        MiniStackIamRole role, MiniStackIamSession session, ExecutorBinding executor, RunnerBootstrapTarget target,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        string shortId = executor.ResourceArn[(executor.ResourceArn.LastIndexOf('/') + 1)..];
        string[] scope = executor.ResourceArn.Split(':');
        var host = await DirectEc2ImdsHost.StartAsync(shortId, scope[4], scope[3], role.Name, session.Credentials, cancellationToken);
        var metadataHttp = Ec2ImdsV2.CreateHttpClient();
        var gate = new OfferGate(cloud.Endpoint);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        AmazonDynamoDBClient? receipt = null;
        try
        {
            var imds = new Ec2ImdsV2(metadataHttp, host.Endpoint, clock);
            var verifier = new Ec2BootstrapIdentity(imds, credentials => new AmazonSecurityTokenServiceClient(credentials, ec2.StsConfig()));
            var authority = new Ec2BootstrapAuthority(scope[4], scope[3], role.Arn, role.RoleId);
            var identity = await verifier.VerifyAsync(authority, lifetime.Token, DirectEc2Fixture.LinuxImage);
            var scoped = cloud.Controller.Configure<AmazonDynamoDBConfig>();
            scoped.HttpClientFactory = gate;
            receipt = new AmazonDynamoDBClient(identity.Credentials, scoped);
            var offers = new DynamoDbBootstrapOfferClient(receipt, cloud.TableName, identity.AwsUserId, identity.ResourceArn, clock);
            var job = new SimulatedJob();
            int spent = 0;
            var runner = new Ec2Bootstrap(offers, target, identity.ResourceArn, identity.AwsUserId,
                () => Interlocked.Exchange(ref spent, 1) == 0, job, clock,
                TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20),
                (hello, token) => BrokerHelloFixture.RegisterAsync(cloud.Controller.DynamoDb, cloud.TableName,
                    identity.AwsUserId, hello, clock, token),
                ReportAsync, telemetryProvider: RunnerProvisionerType.Ec2);
            return new DirectEc2BootstrapExecution(host, metadataHttp, gate, receipt, lifetime, job, identity, runner.RunAsync(lifetime.Token));

            async Task ReportAsync(BootstrapRunnerObservation observation, CancellationToken token)
            {
                var current = await verifier.VerifyAsync(authority, token, DirectEc2Fixture.LinuxImage);
                if (current.AwsUserId != identity.AwsUserId || current.ResourceArn != identity.ResourceArn)
                {
                    throw new InvalidOperationException("EC2 bootstrap identity changed during status publication.");
                }
                using var statusClient = new AmazonDynamoDBClient(current.Credentials, cloud.Controller.Configure<AmazonDynamoDBConfig>());
                var publisher = new DynamoDbEc2BootstrapStatusClient(statusClient, cloud.TableName, current.AwsUserId, current.ResourceArn, clock);
                var result = await publisher.PublishAsync(observation.Hello, new JitEc2BootstrapStatus(observation.OfferId,
                    observation.RunnerId, observation.Exited ? 2 : 1, observation.Exited, clock.GetUtcNow())
                {
                    QualificationSha256 = DirectEc2Fixture.StatusQualificationDigest,
                }, token);
                if (result != LifecycleWriteOutcome.Committed)
                {
                    throw new InvalidOperationException("EC2 bootstrap status was not committed.");
                }
            }
        }
        catch
        {
            lifetime.Dispose();
            receipt?.Dispose();
            gate.Dispose();
            metadataHttp.Dispose();
            await host.DisposeAsync();
            throw;
        }
    }

    internal async Task WaitForReceiptAttemptAsync(CancellationToken cancellationToken)
    {
        var attempted = gate.Attempted;
        var next = await Task.WhenAny(attempted, outcome).WaitAsync(cancellationToken);
        if (next == outcome)
        {
            throw new InvalidOperationException($"Bootstrap ended before the first offer request ({await outcome}).");
        }
        await attempted;
    }

    internal void StartReceipt(long runnerId, string runnerName)
    {
        job.Register(runnerId, runnerName);
        gate.Open();
    }

    internal void FinishJob() => job.Finish();

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        try
        {
            await outcome;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Dispose();
            receipt.Dispose();
            gate.Dispose();
            metadataHttp.Dispose();
            await host.DisposeAsync();
        }
    }

    internal sealed class SimulatedJob : IOneJobRunner
    {
        private readonly TaskCompletionSource<(long Id, string Name)> _registration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _starts;

        internal void Register(long runnerId, string runnerName) => _registration.SetResult((runnerId, runnerName));

        internal void Finish() => _finished.SetResult();

        public async Task<OneJobCompletion> RunAsync(JitPlaintext configuration, string expectedName,
            Func<long, CancellationToken, Task> ready, CancellationToken cancellationToken)
        {
            Assert.Equal(1, Interlocked.Increment(ref _starts));
            Assert.True(configuration.Bytes.SequenceEqual(Encoding.UTF8.GetBytes(GitHubSimulator.Jit)));
            var registration = await _registration.Task.WaitAsync(cancellationToken);
            Assert.Equal(registration.Name, expectedName);
            await ready(registration.Id, cancellationToken);
            await _finished.Task.WaitAsync(cancellationToken);
            return new OneJobCompletion(registration.Id, 0);
        }
    }

    internal sealed class OfferGate(Uri endpoint) : HttpClientFactory, IDisposable
    {
        private readonly TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Attempted => _attempted.Task;

        internal void Open() => _open.TrySetResult();

        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(new GateHandler(this));

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;

        public void Dispose() => _open.TrySetCanceled();

        private sealed class GateHandler(OfferGate owner) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri is not { Scheme: "http", Host: "127.0.0.1" } uri || uri.Authority != owner.Endpoint.Authority)
                {
                    throw new InvalidOperationException("The offer gate accepts the exact loopback MiniStack endpoint.");
                }
                if (request.Headers.TryGetValues("X-Amz-Target", out var targets) &&
                    targets.Contains("DynamoDB_20120810.DeleteItem", StringComparer.Ordinal))
                {
                    owner._attempted.TrySetResult();
                    await owner._open.Task.WaitAsync(cancellationToken);
                }
                return await base.SendAsync(request, cancellationToken);
            }
        }

        private Uri Endpoint { get; } = endpoint;
    }
}
