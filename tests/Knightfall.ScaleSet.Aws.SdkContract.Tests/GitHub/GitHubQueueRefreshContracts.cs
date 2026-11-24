using System.Globalization;
using System.Net;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.GitHub;

public sealed class GitHubQueueRefreshContracts
{
    private const string RefreshedToken = "synthetic-refreshed-queue-token";
    private const string RefreshedQueuePath = "/messages/refreshed-queue";
    private const long MessageId = 6_000_000_004;
    private static readonly Guid ReplacementId = Guid.Parse("774c3a91-2067-4679-98e2-581738abb98e");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueAcknowledgementRetriesOnlyUnderTheSameRefreshedSession(bool replacement)
    {
        string sessionsPath = $"{GitHubWire.ServicePath}/_apis/runtime/runnerscalesets/{GitHubWire.SetId}/sessions";
        string refreshPath = $"{sessionsPath}/{GitHubWire.SessionId:D}";
        string messageSegment = MessageId.ToString(CultureInfo.InvariantCulture);
        string originalAck = $"{GitHubWire.QueuePath}/{messageSegment}";
        string refreshedAck = $"{RefreshedQueuePath}/{messageSegment}";
        string reply = GitHubWire.SessionJson(replacement ? ReplacementId : GitHubWire.SessionId)
            .Replace(GitHubWire.QueueToken, RefreshedToken, StringComparison.Ordinal)
            .Replace(GitHubWire.QueuePath, RefreshedQueuePath, StringComparison.Ordinal);
        using var wire = new GitHubWire((request, _) => request.Uri.AbsolutePath switch
        {
            var path when path == sessionsPath => GitHubWire.Reply(HttpStatusCode.OK, GitHubWire.SessionJson()),
            var path when path == refreshPath => GitHubWire.Reply(HttpStatusCode.OK, reply),
            var path when path == originalAck => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)),
            var path when path == refreshedAck => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)),
            _ => throw new InvalidOperationException("Unexpected request path."),
        });
        using var http = new HttpClient(wire);
        using var client = GitHubWire.Connect(http, new ContractClock());
        var token = TestContext.Current.CancellationToken;
        var session = await client.CreateMessageSessionAsync(GitHubWire.SetId, "owner", token);
        var originalSession = session.Session;

        if (replacement)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => session.DeleteMessageAsync(MessageId, token));
            Assert.Same(originalSession, session.Session);
        }
        else
        {
            await session.DeleteMessageAsync(MessageId, token);
            Assert.Equal(RefreshedToken, session.Session.MessageQueueAccessToken);
        }

        Assert.Equal(GitHubWire.SessionId, session.Session.SessionId);
        var calls = wire.Requests.Where(request => request.Uri.Host is GitHubWire.ServiceHost or GitHubWire.QueueHost)
            .Skip(1).ToArray();
        Assert.Equal(replacement ? 2 : 3, calls.Length);
        Assert.Equal((HttpMethod.Delete, GitHubWire.QueueHost, originalAck, "Bearer " + GitHubWire.QueueToken),
            (calls[0].Method, calls[0].Uri.Host, calls[0].Uri.AbsolutePath, calls[0].Authorization));
        Assert.Equal((HttpMethod.Patch, GitHubWire.ServiceHost, refreshPath, "Bearer " + GitHubWire.AdminToken),
            (calls[1].Method, calls[1].Uri.Host, calls[1].Uri.AbsolutePath, calls[1].Authorization));
        if (!replacement)
        {
            Assert.Equal((HttpMethod.Delete, GitHubWire.QueueHost, refreshedAck, "Bearer " + RefreshedToken),
                (calls[2].Method, calls[2].Uri.Host, calls[2].Uri.AbsolutePath, calls[2].Authorization));
        }
    }
}
