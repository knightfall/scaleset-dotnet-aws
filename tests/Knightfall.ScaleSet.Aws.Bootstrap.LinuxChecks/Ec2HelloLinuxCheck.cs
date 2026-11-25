using System.Net;
using System.Runtime.Versioning;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;
using OneJobBootstrap = Knightfall.ScaleSet.Aws.RunnerBootstrap.RunnerBootstrap;

namespace Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks;

[SupportedOSPlatform("linux")]
internal static class Ec2HelloLinuxCheck
{
    internal static async Task VerifySpentMarkerAsync(string privateState, IOneJobRunner runner, CancellationToken cancellationToken)
    {
        const string region = "ap-southeast-2";
        const string resource = "arn:aws:ec2:ap-southeast-2:111122223333:instance/i-0123456789abcdef2";
        const string principal = "AROAFIXTUREIDENTITY0001:i-0123456789abcdef2";
        var credentials = new SessionAWSCredentials("fixture", "fixture", "fixture");
        using var dynamo = new AmazonDynamoDBClient(credentials, new AmazonDynamoDBConfig
        {
            AuthenticationRegion = region,
            ServiceURL = "http://127.0.0.1:1",
            IgnoreConfiguredEndpointUrls = true,
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
        });
        using var wire = new ThrottledHelloWire();
        using var transport = new HttpClient(wire, disposeHandler: false);
        var clock = new ImmediateBackoffClock();
        var registration = new HelloEndpointRegistration(transport, "https://fixture.lambda-url.ap-southeast-2.on.aws/", region,
            credentials, clock);
        var target = new RunnerBootstrapTarget("111122223333:ap-southeast-2:fixture", "ec2", "github.com/fixture/repo", 12);
        int registrations = 0;
        OneJobBootstrap Bootstrap() => new(new DynamoDbBootstrapOfferClient(dynamo, "offline", principal, resource, clock),
            target, resource, principal, new Ec2SpentMarker(privateState, resource).TryReserve, runner,
            clock, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2), (hello, token) =>
            {
                registrations++;
                return registration.RegisterAsync(hello, token);
            });

        if (await Bootstrap().RunAsync(cancellationToken) != RunnerBootstrapOutcome.HelloRejected ||
            await Bootstrap().RunAsync(cancellationToken) != RunnerBootstrapOutcome.AlreadySpent || registrations != 1 || wire.Sends != 7 ||
            Directory.GetFiles(privateState, "*.spent").Length != 2)
        {
            throw new InvalidOperationException("Throttled HELLO did not retain the actual persistent EC2 spent marker.");
        }
    }

    private sealed class ThrottledHelloWire : HttpMessageHandler
    {
        internal int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }
    }

    private sealed class ImmediateBackoffClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime < TimeSpan.FromSeconds(5) ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, period);
    }
}
