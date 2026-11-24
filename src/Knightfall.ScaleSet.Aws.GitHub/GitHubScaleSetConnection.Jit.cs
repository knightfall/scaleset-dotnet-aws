using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed partial class GitHubScaleSetConnection
{
    internal async Task<RunnerScaleSetJitRunnerConfig> GenerateJitAsync(
        EffectSendAuthorization permission, CancellationToken cancellationToken)
    {
        if (permission.Envelope.Request is not GitHubJitGenerateEffectRequest request || request.ScopeIdentity != ScopeIdentity)
        {
            throw BindingMismatch();
        }
        var result = await RunBoundMutationAsync(permission.TryConsume, token => _client.GenerateJitConfigAsync(request.ScaleSetId,
            new RunnerScaleSetJitRunnerSetting { Name = request.RunnerName, WorkFolder = request.WorkFolder }, token),
            cancellationToken).ConfigureAwait(false);
        if (result.Runner is null || string.IsNullOrWhiteSpace(result.EncodedJitConfig))
        {
            throw BindingMismatch();
        }
        RequireRunner(result.Runner, request.ScaleSetId, request.RunnerName);
        return result;
    }

    internal async Task<RunnerReference?> FindExactRunnerAsync(
        GitHubJitGenerateEffectRequest request, CancellationToken cancellationToken)
    {
        if (request.ScopeIdentity != ScopeIdentity)
        {
            throw BindingMismatch();
        }
        var candidate = await InvokeAsync(() => _client.FindRunnerByNameAsync(request.RunnerName, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }
        RequireRunner(candidate, request.ScaleSetId, request.RunnerName);
        return await ReadExactRunnerAsync(request.ScopeIdentity, request.ScaleSetId, request.RunnerName, candidate.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<RunnerReference?> ReadExactRunnerAsync(
        string scopeIdentity, long scaleSetId, string runnerName, long runnerId, CancellationToken cancellationToken)
    {
        if (scopeIdentity != ScopeIdentity)
        {
            throw BindingMismatch();
        }
        var runner = await InvokeAsync(() => _client.GetRunnerAsync(runnerId, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (runner is not null)
        {
            RequireRunner(runner, scaleSetId, runnerName);
            if (runner.Id != runnerId)
            {
                throw BindingMismatch();
            }
        }
        return runner;
    }

    internal Task<bool> RemoveExactRunnerAsync(EffectSendAuthorization permission, CancellationToken cancellationToken)
    {
        if (permission.Envelope.Request is not GitHubRunnerRemoveEffectRequest request || request.ScopeIdentity != ScopeIdentity)
        {
            throw BindingMismatch();
        }
        return RunBoundMutationAsync(permission.TryConsume, async token =>
        {
            await _client.RemoveRunnerAsync(request.RunnerId, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private static void RequireRunner(RunnerReference runner, long scaleSetId, string name)
    {
        if (runner.Id <= 0 || runner.RunnerScaleSetId != scaleSetId || runner.Name != name)
        {
            throw BindingMismatch();
        }
    }
}
