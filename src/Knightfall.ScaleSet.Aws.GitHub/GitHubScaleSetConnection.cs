using System.Collections.Immutable;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using ScaleSet.Client;

namespace Knightfall.ScaleSet.Aws.GitHub;

public sealed record GitHubScaleSetBinding(
    string ScopeUrl,
    string RunnerGroup,
    long RunnerGroupId,
    string ScaleSet,
    long ScaleSetId,
    ImmutableArray<string> Labels);

public sealed record GitHubScaleSetLookup(long RunnerGroupId, GitHubScaleSetBinding? Binding);

public interface IGitHubScaleSetRemote
{
    string ScopeUrl { get; }
    string ScopeIdentity { get; }

    Task<GitHubScaleSetLookup> LookupAsync(
        RunnerGitHubConfiguration routing, GitHubScaleSetBinding? previouslyBound, CancellationToken cancellationToken);

    Task<long?> FindExactAsync(GitHubScaleSetCreateEffectRequest request, CancellationToken cancellationToken);

    Task<GitHubScaleSetBinding> CreateScaleSetAsync(
        EffectSendAuthorization authorization, CancellationToken cancellationToken);
}

public sealed class GitHubScaleSetClientFactory(
    HttpClient? httpClient = null, ScaleSetClientOptions? options = null, TimeProvider? timeProvider = null)
{
    public GitHubScaleSetConnection Create(GitHubSecretVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        try
        {
            string identity = GitHubRoutingIdentity.Scope(version.Target.SourceUri.AbsoluteUri);
            return new GitHubScaleSetConnection(version.CreateClient(options, httpClient, timeProvider),
                identity, version, timeProvider ?? TimeProvider.System);
        }
        catch (Exception error)
        {
            throw GitHubOperationException.From(error);
        }
    }
}

public sealed class GitHubScaleSetConnection : IGitHubScaleSetRemote, IDisposable
{
    private readonly ScaleSetClient _client;
    private readonly TimeProvider _clock;

    internal GitHubScaleSetConnection(ScaleSetClient client, string scopeIdentity, GitHubSecretVersion version, TimeProvider clock)
    {
        _client = client;
        _clock = clock;
        ScopeIdentity = scopeIdentity;
        int separator = scopeIdentity.IndexOf('/');
        ScopeUrl = "https://" + scopeIdentity[..separator] + "/" +
            string.Join('/', scopeIdentity[(separator + 1)..].Split('/').Select(Uri.EscapeDataString));
        SecretArn = version.SecretArn;
        AuthType = version.AuthType;
        VersionId = version.VersionId;
    }

    public string ScopeUrl { get; }
    public string ScopeIdentity { get; }
    public string VersionId { get; }
    internal string SecretArn { get; }
    internal GitHubAuthType AuthType { get; }

    public async Task<GitHubScaleSetLookup> LookupAsync(
        RunnerGitHubConfiguration routing, GitHubScaleSetBinding? previouslyBound, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(routing);
        long? exactSetId = routing.ScaleSetId;
        if (previouslyBound is { } bound)
        {
            if (!MatchesScope(bound.ScopeUrl) ||
                !MatchesName(bound.RunnerGroup, routing.RunnerGroup) || !MatchesName(bound.ScaleSet, routing.ScaleSet) ||
                bound.Labels.IsDefaultOrEmpty || !MatchLabels(bound.Labels, routing.Labels) ||
                (exactSetId.HasValue && exactSetId != bound.ScaleSetId))
            {
                throw BindingMismatch();
            }
            exactSetId = bound.ScaleSetId;
        }
        if (exactSetId is <= 0)
        {
            throw BindingMismatch();
        }

        var group = await ReadGroupAsync(routing.RunnerGroup, previouslyBound?.RunnerGroupId, cancellationToken)
            .ConfigureAwait(false);
        var set = await InvokeAsync(
            () => exactSetId is long id
                ? _client.GetScaleSetAsync(id, cancellationToken)
                : _client.FindScaleSetAsync(group.Id, routing.ScaleSet, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (set is null)
        {
            return exactSetId.HasValue ? throw BindingMismatch() : new GitHubScaleSetLookup(group.Id, null);
        }

        var binding = CheckBinding(set, routing.RunnerGroup, group.Id, routing.ScaleSet, routing.Labels, exactSetId);
        return new GitHubScaleSetLookup(group.Id, binding);
    }

    public async Task<long?> FindExactAsync(GitHubScaleSetCreateEffectRequest request, CancellationToken cancellationToken)
    {
        RequireScope(request);
        var group = await ReadGroupAsync(request.RunnerGroupName, request.RunnerGroupId, cancellationToken).ConfigureAwait(false);
        var set = await InvokeAsync(() => _client.FindScaleSetAsync(group.Id, request.ScaleSetName, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        return set is null ? null : CheckBinding(
            set, request.RunnerGroupName, group.Id, request.ScaleSetName, request.Labels, null).ScaleSetId;
    }

    public async Task<GitHubScaleSetBinding> CreateScaleSetAsync(
        EffectSendAuthorization authorization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.Envelope.Request is not GitHubScaleSetCreateEffectRequest request)
        {
            throw BindingMismatch();
        }
        RequireScope(request);
        cancellationToken.ThrowIfCancellationRequested();
        long starting = _clock.GetTimestamp();
        if (!authorization.TryConsume(out TimeSpan remaining))
        {
            throw new GitHubOperationException(remaining > TimeSpan.Zero
                ? GitHubOperationError.AuthorizationConsumed
                : GitHubOperationError.DeadlineElapsed);
        }
        DateTimeOffset local = _clock.GetUtcNow();
        remaining -= _clock.GetElapsedTime(starting);
        if (remaining <= TimeSpan.Zero)
        {
            throw new GitHubOperationException(GitHubOperationError.DeadlineElapsed);
        }
        DateTimeOffset deadline = local + remaining;
        using var timeout = new CancellationTokenSource(remaining, _clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        using var singleAttempt = ScaleSetRequestRetry.Configure(options => options with
        {
            MaxRetries = 0,
            Deadline = options.Deadline is { } existing && existing < deadline ? existing : deadline,
        });
        var set = await InvokeAsync(
            () => _client.CreateScaleSetAsync(new RunnerScaleSet
            {
                Name = request.ScaleSetName,
                RunnerGroupId = request.RunnerGroupId,
                Labels = request.Labels.Select(label => new Label { Name = label, Type = "System" }).ToArray(),
            }, bounded.Token),
            cancellationToken).ConfigureAwait(false);
        return CheckBinding(set, request.RunnerGroupName, request.RunnerGroupId, request.ScaleSetName, request.Labels, null);
    }

    public void Dispose() => _client.Dispose();

    private async Task<RunnerGroup> ReadGroupAsync(string name, long? expectedId, CancellationToken cancellationToken)
    {
        var group = await InvokeAsync(() => _client.GetRunnerGroupByNameAsync(name, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (group.Id < 0 || !MatchesName(group.Name, name) || (expectedId.HasValue && group.Id != expectedId))
        {
            throw BindingMismatch();
        }
        return group;
    }

    private GitHubScaleSetBinding CheckBinding(
        RunnerScaleSet set, string groupName, long groupId, string setName, ImmutableArray<string> labels, long? setId)
    {
        if (set.Id <= 0 || (setId.HasValue && set.Id != setId) || set.RunnerGroupId != groupId ||
            (!string.IsNullOrEmpty(set.RunnerGroupName) && !MatchesName(set.RunnerGroupName, groupName)) ||
            !MatchesName(set.Name, setName) || set.Labels is null ||
            !MatchLabels(set.Labels.Select(label => label?.Name), labels))
        {
            throw BindingMismatch();
        }

        return new GitHubScaleSetBinding(ScopeUrl, groupName, groupId, setName, set.Id, labels);
    }

    private void RequireScope(GitHubScaleSetCreateEffectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MatchesScope(request.ScopeUrl))
        {
            throw BindingMismatch();
        }
    }

    private bool MatchesScope(string scope)
    {
        try
        {
            return GitHubRoutingIdentity.Scope(scope) == ScopeIdentity;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<T> InvokeAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception error)
        {
            throw GitHubOperationException.From(error);
        }
    }

    private static bool MatchesName(string? actual, string expected) =>
        TryFoldName(actual, out string actualFolded) && TryFoldName(expected, out string expectedFolded) &&
        actualFolded == expectedFolded;

    private static bool MatchLabels(IEnumerable<string?> actual, ImmutableArray<string> expected)
    {
        if (expected.IsDefaultOrEmpty)
        {
            return false;
        }

        var remaining = new HashSet<string>(StringComparer.Ordinal);
        foreach (string label in expected)
        {
            if (!TryFoldName(label, out string folded) || !remaining.Add(folded))
            {
                return false;
            }
        }
        foreach (string? label in actual)
        {
            if (!TryFoldName(label, out string folded) || !remaining.Remove(folded))
            {
                return false;
            }
        }
        return remaining.Count == 0;
    }

    private static bool TryFoldName(string? name, out string folded)
    {
        folded = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        try
        {
            folded = GitHubRoutingIdentity.Name(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static GitHubOperationException BindingMismatch() => new(GitHubOperationError.BindingMismatch);
}
