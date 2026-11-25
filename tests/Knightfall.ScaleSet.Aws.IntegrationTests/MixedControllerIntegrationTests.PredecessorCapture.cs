using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Web;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class MixedControllerIntegrationTests
{
    [Fact(Timeout = 125_000)]
    public async Task CapturedTerminalPredecessorRecoversJournalAndRetiresTheExactSessionAfterRestart()
    {
        var token = TestContext.Current.CancellationToken;
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? closePath = null;
        await using var fixture = await MixedRuntimeFixture.CreateAsync("ec2", token, qualifyCapture: true, beforeGitHubRequest: async context =>
        {
            if (HttpMethods.IsDelete(context.Request.Method) && context.Request.Path == Volatile.Read(ref closePath))
            {
                closeEntered.TrySetResult();
                await releaseClose.Task.WaitAsync(context.RequestAborted);
            }
        });
        string deployment = ControllerLeaseRecoveryCoordinator.DeploymentKey(fixture.Configuration);
        string firstTask = await fixture.StartControllerTaskAsync(token);
        string secondTask = await fixture.StartControllerTaskAsync(token);
        var profile = fixture.Configuration.Profiles[0];
        var provisioner = Assert.IsType<Ec2ProvisionerConfiguration>(profile.Provisioner);
        var key = new LifecycleProfile(deployment, profile.Id);
        var github = fixture.Profiles[profile.Id].GitHub;
        await using var first = MixedControllerProcess.Start(fixture, firstTask, telemetryEndpoint: null);
        await WaitAsync(async () => github.SessionId.HasValue && await first.ReadyAsync(token) == HttpStatusCode.OK,
            "the former controller session", token);
        var priorSession = Assert.IsType<Guid>(github.SessionId);
        fixture.Cloud.HoldNextEc2Launch(provisioner.LaunchTemplateId);
        github.TotalAssignedJobs = 1;
        github.Offer(8_334_600_001);
        github.Offer(8_334_600_001);
        var heldLaunch = await fixture.Cloud.HeldLaunch.WaitAsync(TimeSpan.FromSeconds(35), token);
        string priorPollPath = "/messages/" + priorSession.ToString("D");
        await WaitAsync(() => Task.FromResult(github.Requests.Any(request => request.Path == priorPollPath &&
            long.TryParse(request.Cursor, CultureInfo.InvariantCulture, out long cursor) && cursor > 0)), "the prior session advancing its cursor", token);
        var journal = new DynamoDbEffectJournal(fixture.Store, TimeProvider.System);
        string intent = EffectIntentIdentity.ForEc2Launch(heldLaunch.AllocationId, 0);
        var unresolved = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(key, intent, token));
        Assert.Equal(EffectState.Executing, unresolved.Entry.State);
        await first.KillAsync(token);
        var previous = Assert.IsType<ControllerLease>(await fixture.Store.ReadLeaseAsync(deployment, token));
        DateTimeOffset acquiredAt = Assert.IsType<DateTimeOffset>(previous.AcquiredAt);
        Assert.Equal(firstTask, previous.OwnerTaskArn);
        await fixture.Cloud.StopControllerTaskAsync(fixture.ControllerCluster, firstTask, token);
        fixture.Cloud.ExpireStoppedTask(fixture.ControllerCluster, firstTask);
        Volatile.Write(ref closePath, GitHubSimulator.Sessions + $"/{priorSession:D}");

        await using var successor = MixedControllerProcess.Start(fixture, secondTask, telemetryEndpoint: null);
        try
        {
            await WaitAsync(async () => await fixture.Store.ReadLeaseAsync(deployment, token) is
                    { Mode: ControllerLeaseMode.RecoveryOnly } held && held.OwnerTaskArn == secondTask &&
                fixture.Cloud.Calls.Any(call => call is { Service: "logs", Operation: "FilterLogEvents" }), "unavailable predecessor evidence", token);
            var recovery = Assert.IsType<ControllerLease>(await fixture.Store.ReadLeaseAsync(deployment, token));
            Assert.Equal(previous.Epoch + 1, recovery.Epoch);
            Assert.Equal((firstTask, acquiredAt), (recovery.RequiredFencingTaskArn, recovery.RequiredFencingTaskAcquiredAt));
            Assert.Equal(priorSession, github.SessionId);
            Assert.False(closeEntered.Task.IsCompleted);
            Assert.False(fixture.Cloud.RecoveryRead.IsCompleted);
            var unchanged = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(key, intent, token));
            Assert.Equal((unresolved.Revision, unresolved.TransitionId, unresolved.Entry.Attempt?.Id, unresolved.Entry.Envelope.Sha256),
                (unchanged.Revision, unchanged.TransitionId, unchanged.Entry.Attempt?.Id, unchanged.Entry.Envelope.Sha256));
            Assert.Equal(EffectState.Executing, unchanged.Entry.State);
            Assert.Null(unchanged.Entry.Resolution);

            fixture.Cloud.PublishCapturedStop(acquiredAt);
            await fixture.Cloud.RecoveryRead.WaitAsync(TimeSpan.FromSeconds(20), token);
            var continuations = fixture.Cloud.Calls.Where(call => call is { Service: "logs", Operation: "FilterLogEvents" })
                .Select(CaptureWindow).Where(window => window.Token is not null).ToArray();
            Assert.Contains(continuations, window => window.Token == "6");
            Assert.Contains(continuations, window => window.Token == "8");
            Assert.Single(continuations.Select(window => window.End).Distinct());
            Assert.All(continuations, window => Assert.Equal(acquiredAt.ToUnixTimeSeconds() * 1000, window.Start));
            Assert.Equal(ControllerLeaseMode.RecoveryOnly, (await fixture.Store.ReadLeaseAsync(deployment, token))?.Mode);
            Assert.Equal(priorSession, github.SessionId);
            Assert.False(closeEntered.Task.IsCompleted);
            fixture.Cloud.ReleaseRecoveryRead();

            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            Assert.Equal(priorSession, github.SessionId);
            var closing = Assert.IsType<SessionRecord>(await fixture.Store.ReadSessionAsync(key, token));
            Assert.Equal(SessionPhase.Closing, closing.Phase);
            Assert.Equal(SessionCloseKind.FencedRetirement, closing.CloseKind);
            Assert.Equal(priorSession, closing.SessionId);
            Assert.Single(github.Requests, request => request is { Method: "POST", Path: var path } && path == GitHubSimulator.Sessions);
            var settled = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(key, intent, token));
            Assert.Equal(EffectState.Settled, settled.Entry.State);
            Assert.Equal(EffectResolutionKind.Applied, settled.Entry.Resolution?.Kind);
            Assert.Equal(unresolved.Entry.Attempt?.Id, settled.Entry.Attempt?.Id);
            var executor = Assert.IsType<ExecutorSnapshot>(await fixture.Store.ReadExecutorAsync(key, heldLaunch.AllocationId.ToString("D"), token));
            Assert.Equal(heldLaunch.ResourceArn, executor.Binding.ResourceArn);
            Assert.Single(fixture.Cloud.Calls, call => call is { Service: "ec2", Operation: "RunInstances" } &&
                HttpUtility.ParseQueryString(call.Request)["ClientToken"] == heldLaunch.ClientToken);

            releaseClose.TrySetResult();
            await WaitAsync(() => Task.FromResult(github.SessionId is Guid session && session != priorSession &&
                github.Requests.Any(request => request.Path == $"/messages/{session:D}")), "the successor session's first poll", token);
            var successorSession = Assert.IsType<Guid>(github.SessionId);
            Assert.Single(github.Requests, request => request.Method == "DELETE" && request.Path == closePath);
            Assert.Empty(github.Requests.First(request => request.Path == $"/messages/{successorSession:D}").Cursor);
            Assert.Equal(successorSession, (await fixture.Store.ReadSessionAsync(key, token))?.SessionId);
        }
        finally
        {
            releaseClose.TrySetResult();
            await successor.KillAsync(CancellationToken.None);
            output.WriteLine(await successor.DiagnosticsAsync());
        }
    }

    private static (long Start, long End, string? Token) CaptureWindow(MixedCloudCall call)
    {
        using var document = JsonDocument.Parse(call.Request);
        var request = document.RootElement;
        return (request.GetProperty("startTime").GetInt64(), request.GetProperty("endTime").GetInt64(),
            request.TryGetProperty("nextToken", out var token) ? token.GetString() : null);
    }
}
