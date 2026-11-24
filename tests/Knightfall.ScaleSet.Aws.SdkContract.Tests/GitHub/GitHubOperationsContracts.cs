using System.Globalization;
using System.Net;
using System.Text.Json;
using ScaleSet.Client;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;

public sealed class GitHubOperationsContracts
{
    private const string Sets = GitHubWire.ServicePath + "/_apis/runtime/runnerscalesets";
    private const string Groups = GitHubWire.ServicePath + "/_apis/runtime/runnergroups/";
    private const string Runners = GitHubWire.ServicePath + "/_apis/distributedtask/pools/0/agents";
    private const string RunnerName = "runner & canary";
    private const string GroupName = "Build & Test";
    private const string QueueJobType = "RunnerScaleSetJobMessages";
    private static string Set => $"{Sets}/{GitHubWire.SetId}";
    private static string Runner => $"{Runners}/{GitHubWire.RunnerId}";

    [Fact]
    public async Task AdministrationUsesScopedQueryFieldsAndPreserves64BitIds()
    {
        string scaleSet = $$"""{"id":{{GitHubWire.SetId}},"runnerGroupId":{{GitHubWire.GroupId}},"name":"{{RunnerName}}"}""";
        string runner = $$"""{"id":{{GitHubWire.RunnerId}},"runnerScaleSetId":{{GitHubWire.SetId}},"name":"{{RunnerName}}"}""";
        using var wire = new GitHubWire((request, _) =>
        {
            var body = request.Uri.AbsolutePath switch
            {
                Groups => $$"""{"count":1,"value":[{"id":{{GitHubWire.GroupId}},"name":"{{GroupName}}"}]}""",
                Sets when request.Method == HttpMethod.Post => scaleSet,
                Sets => $$"""{"count":1,"value":[{{scaleSet}}]}""",
                Runners => $$"""{"count":1,"value":[{{runner}}]}""",
                var path when path == Set => scaleSet,
                var path when path == Runner => runner,
                var path when path == Set + "/generatejitconfig" => $$"""{"runner":{{runner}},"encodedJITConfig":"opaque-jit"}""",
                _ => throw new InvalidOperationException(request.Uri.AbsolutePath),
            };
            return request.Method == HttpMethod.Delete
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))
                : GitHubWire.Reply(HttpStatusCode.OK, body);
        });
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, new ContractClock());
        var token = TestContext.Current.CancellationToken;

        var group = await client.GetRunnerGroupByNameAsync(GroupName, token);
        var found = await client.FindScaleSetAsync(GitHubWire.GroupId, RunnerName, token);
        var listed = Assert.Single(await client.ListScaleSetsAsync(GitHubWire.GroupId, token));
        var readSet = await client.GetScaleSetAsync(GitHubWire.SetId, token);
        var created = await client.CreateScaleSetAsync(new RunnerScaleSet { Name = RunnerName, RunnerGroupId = GitHubWire.GroupId }, token);
        var updated = await client.UpdateScaleSetAsync(GitHubWire.SetId,
            new RunnerScaleSet { Labels = [new Label { Name = "custom-label" }] }, token);
        var jit = await client.GenerateJitConfigAsync(GitHubWire.SetId, new RunnerScaleSetJitRunnerSetting { Name = RunnerName }, token);
        var readRunner = await client.GetRunnerAsync(GitHubWire.RunnerId, token);
        var foundRunner = await client.FindRunnerByNameAsync(RunnerName, token);
        await client.RemoveRunnerAsync(GitHubWire.RunnerId, token);

        Assert.Equal(GitHubWire.GroupId, group.Id);
        Assert.Equal(GroupName, group.Name);
        Assert.All(new[] { found, listed, readSet, created, updated }, value =>
        {
            Assert.NotNull(value);
            Assert.Equal(GitHubWire.SetId, value.Id);
            Assert.Equal(GitHubWire.GroupId, value.RunnerGroupId);
            Assert.Equal(RunnerName, value.Name);
        });
        Assert.All(new[] { jit.Runner, readRunner, foundRunner }, value =>
        {
            Assert.NotNull(value);
            Assert.Equal(GitHubWire.RunnerId, value.Id);
            Assert.Equal(GitHubWire.SetId, value.RunnerScaleSetId);
        });
        Assert.Equal("opaque-jit", jit.EncodedJitConfig);
        Assert.DoesNotContain(jit.EncodedJitConfig, jit.ToString(), StringComparison.Ordinal);

        var requests = wire.Requests.Where(request => request.Uri.Host == GitHubWire.ServiceHost).ToArray();
        Assert.Equal(new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, Groups), (HttpMethod.Get, Sets), (HttpMethod.Get, Sets),
            (HttpMethod.Get, Set), (HttpMethod.Post, Sets), (HttpMethod.Patch, Set), (HttpMethod.Post, Set + "/generatejitconfig"),
            (HttpMethod.Get, Runner), (HttpMethod.Get, Runners), (HttpMethod.Delete, Runner),
        }, requests.Select(request => (request.Method, request.Uri.AbsolutePath)));
        Assert.All(requests, request => Assert.Equal("Bearer " + GitHubWire.AdminToken, request.Authorization));
        Assert.Equal(GroupName, Query(requests[0].Uri)["groupName"]);
        Assert.Equal(RunnerName, Query(requests[1].Uri)["name"]);
        Assert.Equal(GitHubWire.GroupId.ToString(CultureInfo.InvariantCulture), Query(requests[1].Uri)["runnerGroupId"]);
        Assert.Equal(GitHubWire.GroupId.ToString(CultureInfo.InvariantCulture), Query(requests[2].Uri)["runnerGroupId"]);
        Assert.DoesNotContain("name", Query(requests[2].Uri).Keys);
        Assert.Equal(RunnerName, Query(requests[8].Uri)["agentName"]);
        using var create = JsonDocument.Parse(requests[4].Body!);
        Assert.Equal(GitHubWire.GroupId, create.RootElement.GetProperty("runnerGroupId").GetInt64());
        Assert.Equal(RunnerName, create.RootElement.GetProperty("name").GetString());
        using var patch = JsonDocument.Parse(requests[5].Body!);
        var label = Assert.Single(patch.RootElement.GetProperty("labels").EnumerateArray());
        Assert.Equal("custom-label", label.GetProperty("name").GetString());
        Assert.Equal("System", label.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "JobStillRunningException", ScaleSetDomainError.JobStillRunning)]
    [InlineData(HttpStatusCode.NotFound, "AgentNotFoundException", ScaleSetDomainError.AgentNotFound)]
    public async Task RunnerRemovalDistinguishesBusyAndMissing(
        HttpStatusCode status, string type, ScaleSetDomainError domainError)
    {
        using var wire = new GitHubWire((_, _) => GitHubWire.Reply(status,
            $$"""{"typeName":"Microsoft.VisualStudio.Services.AgentPool.{{type}}","message":"synthetic failure"}"""));
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, new ContractClock());

        var error = await Assert.ThrowsAsync<ScaleSetApiException>(() =>
            client.RemoveRunnerAsync(GitHubWire.RunnerId, TestContext.Current.CancellationToken));

        Assert.Equal((int)status, error.StatusCode);
        Assert.Equal(domainError, error.DomainError);
        var request = Assert.Single(wire.Requests, request => request.Uri.Host == GitHubWire.ServiceHost);
        Assert.Equal((HttpMethod.Delete, Runner), (request.Method, request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task SessionUsesQueueCredentialForPollingAcquisitionAndAckAndAdminForClose()
    {
        const long requestId = 6_000_000_004;
        string message = JsonSerializer.Serialize(new
        {
            messageId = 0,
            messageType = QueueJobType,
            body = $$"""[{"messageType":"JobAvailable","runnerRequestId":{{requestId}},"requestLabels":["linux"]}]""",
        });
        int polls = 0;
        using var wire = new GitHubWire((request, _) =>
        {
            if (request.Method == HttpMethod.Get && request.Uri.Host == GitHubWire.QueueHost)
            {
                return polls++ == 0
                    ? GitHubWire.Reply(HttpStatusCode.OK, message)
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }
            if (request.Uri.AbsolutePath == Set + "/sessions" && request.Method == HttpMethod.Post)
            {
                return GitHubWire.Reply(HttpStatusCode.OK, GitHubWire.SessionJson());
            }
            if (request.Uri.AbsolutePath == Set + "/acquirejobs" && request.Method == HttpMethod.Post)
            {
                return GitHubWire.Reply(HttpStatusCode.OK, $$"""{"count":1,"value":[{{requestId}}]}""");
            }
            if (request.Method == HttpMethod.Delete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            throw new InvalidOperationException(request.Uri.AbsolutePath);
        });
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, new ContractClock());
        var token = TestContext.Current.CancellationToken;
        var session = await client.CreateMessageSessionAsync(GitHubWire.SetId, "owner", token);
        Assert.Equal(GitHubWire.SessionId, session.Session.SessionId);
        Assert.Equal(3, session.Session.Statistics?.TotalAssignedJobs);

        var delivery = await session.GetMessageAsync(0, 0, token);
        Assert.NotNull(delivery);
        Assert.Equal(0L, delivery.MessageId);
        Assert.Equal(requestId, Assert.Single(delivery.JobAvailableMessages).RunnerRequestId);
        Assert.Equal(requestId, Assert.Single(await session.AcquireJobsAsync([requestId], token)));
        await session.DeleteMessageAsync(delivery.MessageId, token);
        Assert.Null(await session.GetMessageAsync(requestId, 7, token));
        await session.CloseAsync(token);
        await session.CloseAsync(token);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetMessageAsync(0, 0, token));

        var requests = wire.Requests.Where(request => request.Uri.Host == GitHubWire.ServiceHost ||
            request.Uri.Host == GitHubWire.QueueHost).ToArray();
        Assert.Equal(new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, Set + "/sessions"), (HttpMethod.Get, GitHubWire.QueuePath),
            (HttpMethod.Post, Set + "/acquirejobs"), (HttpMethod.Delete, GitHubWire.QueuePath + "/0"),
            (HttpMethod.Get, GitHubWire.QueuePath), (HttpMethod.Delete, $"{Set}/sessions/{GitHubWire.SessionId:D}"),
        }, requests.Select(request => (request.Method, request.Uri.AbsolutePath)));
        Assert.Equal("Bearer " + GitHubWire.AdminToken, requests[0].Authorization);
        Assert.Equal("Bearer " + GitHubWire.AdminToken, requests[^1].Authorization);
        Assert.All(requests[1..^1], request => Assert.Equal("Bearer " + GitHubWire.QueueToken, request.Authorization));
        Assert.Equal("0", requests[1].Capacity);
        Assert.DoesNotContain("lastMessageId", Query(requests[1].Uri).Keys);
        Assert.Equal($"[{requestId}]", requests[2].Body);
        Assert.Equal("7", requests[^2].Capacity);
        Assert.Equal(requestId.ToString(CultureInfo.InvariantCulture), Query(requests[^2].Uri)["lastMessageId"]);
        Assert.Equal("synthetic-queue-signature", Query(requests[^2].Uri)["signature"]);
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part[1]), StringComparer.Ordinal);
}
