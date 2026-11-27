using ScaleSet.Client;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed class GitHubSimulatorContractTests
{
    [Theory]
    [InlineData(null, GitHubProtocolHost.PublicApi, GitHubProtocolHost.QueueHost)]
    [InlineData("example", "api.example.ghe.com", "queue-fixture.actions.example.ghe.com")]
    public async Task PinnedClientPollsAnExclusiveQueueOverLoopbackHttp2(string? tenant, string apiHost, string queueHost)
    {
        const string Group = "No-Docker";
        var cancellationToken = TestContext.Current.CancellationToken;
        var simulator = new GitHubSimulator(Group, tenant is null ? null : GitHubSimulatorAddress.ForProfile(tenant))
        {
            TotalAssignedJobs = 3,
        };
        await using var host = await GitHubProtocolHost.StartAsync(simulator, cancellationToken);
        using var http = host.CreateClient();
        string webHost = tenant is null ? "github.com" : $"{tenant}.ghe.com";
        using var client = new ScaleSetClient(GitHubTarget.Parse($"https://{webHost}/enterprises/engineering"),
            ScaleSetCredentials.FromPersonalAccessToken(GitHubSimulator.FirstPat), httpClient: http);
        var group = await client.GetRunnerGroupByNameAsync(Group, cancellationToken);
        Assert.Equal(GitHubSimulator.GroupId, group.Id);
        var set = await client.CreateScaleSetAsync(new RunnerScaleSet { Name = "linux", RunnerGroupId = group.Id,
            Labels = [new Label { Name = "linux", Type = "System" }] }, cancellationToken);
        Assert.Equal(GitHubSimulator.SetId, set.Id);
        var session = await client.CreateMessageSessionAsync(set.Id, "owner", cancellationToken);
        Assert.Equal(3, session.Session.Statistics?.TotalAssignedJobs);
        Assert.Null(await session.GetMessageAsync(0, 2, cancellationToken));
        Assert.Equal(0, simulator.Offer(78));
        var first = await session.GetMessageAsync(0, 2, cancellationToken);
        Assert.Equal(0, Assert.IsType<RunnerScaleSetMessage>(first).MessageId);
        Assert.Equal(78, Assert.Single(first!.JobAvailableMessages).RunnerRequestId);
        Assert.Equal([78L], await session.AcquireJobsAsync([78], cancellationToken));
        await session.DeleteMessageAsync(0, cancellationToken);
        Assert.Equal(1, simulator.Offer(79));
        Assert.Equal(1, (await session.GetMessageAsync(0, 0, cancellationToken))?.MessageId);
        Assert.Empty(await session.AcquireJobsAsync([79], cancellationToken));
        await session.DeleteMessageAsync(1, cancellationToken);
        Assert.Null(await session.GetMessageAsync(1, 0, cancellationToken));
        await session.CloseAsync(cancellationToken);
        Assert.Null(simulator.SessionId);
        Assert.All(simulator.Requests, request => Assert.Equal("HTTP/2", request.Protocol));
        Assert.Contains(simulator.Requests, request => request.Host == apiHost &&
            request.Path == "/enterprises/engineering/actions/runners/registration-token");
        Assert.Contains(simulator.Requests, request => request.Host == queueHost &&
            request.Method == "GET" && request.Cursor == "1" && request.Capacity == "0");
    }

    [Theory]
    [InlineData(429)]
    [InlineData(403)]
    public async Task HostedEnterpriseUsesItsOwnRateLimitedApi(int expectedStatus)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var simulator = new GitHubSimulator("No-Docker", GitHubSimulatorAddress.ForProfile("example"));
        await using var host = await GitHubProtocolHost.StartAsync(simulator, cancellationToken);
        using var http = host.CreateClient();
        using var client = new ScaleSetClient(GitHubTarget.Parse("https://example.ghe.com/enterprises/engineering"),
            ScaleSetCredentials.FromPersonalAccessToken(GitHubSimulator.FirstPat), httpClient: http);
        var set = await client.CreateScaleSetAsync(new RunnerScaleSet { Name = "linux", RunnerGroupId = GitHubSimulator.GroupId,
            Labels = [new Label { Name = "linux", Type = "System" }] }, cancellationToken);
        var session = await client.CreateMessageSessionAsync(set.Id, "owner", cancellationToken);
        simulator.FailNext(SimulatorOperation.Poll,
            expectedStatus == 429 ? SimulatorFault.RateLimited : SimulatorFault.ForbiddenRateLimit);
        using var retries = ScaleSetRequestRetry.Configure(options => options with { MaxRetries = 0 });
        var failure = await Assert.ThrowsAsync<ScaleSetRateLimitException>(() => session.GetMessageAsync(0, 0, cancellationToken));
        Assert.Equal(expectedStatus, failure.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), failure.RetryAfter);
        Assert.Contains(simulator.Requests, request => request.Host == GitHubProtocolHost.EnterpriseApi &&
            request.Path == "/enterprises/engineering/actions/runners/registration-token");
        Assert.DoesNotContain(simulator.Requests, request => request.Host == GitHubProtocolHost.PublicApi);
    }
}
