using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal enum SimulatorOperation
{
    CreateScaleSet = 0,
    OpenSession = 1,
    CloseSession = 2,
    Poll = 3,
    Acknowledge = 4,
    GenerateJit = 5,
    RemoveRunner = 6,
}

internal enum SimulatorFault
{
    DropAfterCommit = 0,
    BadRequest = 1,
    NotFound = 2,
    RateLimited = 3,
    ForbiddenRateLimit = 4,
}

internal sealed record SimulatorRequest(string Method, string Host, string Path, string Protocol, string Cursor, string Capacity);

internal sealed record GitHubSimulatorAddress(string[] ApiHosts, string ServiceHost, string QueueHost)
{
    internal static readonly GitHubSimulatorAddress Default = new(
        [GitHubProtocolHost.PublicApi], GitHubProtocolHost.ServiceHost, GitHubProtocolHost.QueueHost);

    internal IEnumerable<string> Hosts => ApiHosts.Append(ServiceHost).Append(QueueHost);

    internal static GitHubSimulatorAddress ForProfile(string profileId) => new(
        [$"api.{profileId}.ghe.com"], $"service-fixture.actions.{profileId}.ghe.com", $"queue-fixture.actions.{profileId}.ghe.com");
}

internal sealed class GitHubSimulator(string groupName, GitHubSimulatorAddress? address = null)
{
    internal const long GroupId = 203;
    internal const long SetId = 401;
    internal const string AppAccessToken = "fixture-app-installation";
    internal const string FirstPat = "ghp_FixtureOnlyFirstNeverValid";
    internal const string NextPat = "ghp_FixtureOnlySecondNeverValid";
    internal const string Jit = "synthetic-only-one-job-config";
    internal const string ServiceBase = "/tenant";
    internal const string Sets = ServiceBase + "/_apis/runtime/runnerscalesets";
    internal const string Runners = ServiceBase + "/_apis/distributedtask/pools/0/agents";
    internal static readonly string Sessions = FormattableString.Invariant($"{Sets}/{SetId}/sessions");
    internal static readonly string ExactSet = FormattableString.Invariant($"{Sets}/{SetId}");
    private const string RegistrationToken = "fixture-registration-token";
    private const string AdminToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjQwNzA5MDg4MDB9.fixture-signature";
    private const string AsRequested = "RunnerScaleSetJobMessages";
    private static readonly string[] DefaultLabels = ["linux"];
    private readonly Lock _sync = new();
    private readonly ConcurrentQueue<SimulatorRequest> _requests = new();
    private readonly Dictionary<SimulatorOperation, SimulatorFault> _faults = [];
    private readonly Dictionary<long, SimulatedRunner> _runners = [];
    private readonly SortedDictionary<long, SimulatedMessage> _queue = [];
    private readonly HashSet<long> _available = [];
    private readonly HashSet<long> _acquired = [];
    private SimulatedScaleSet? _scaleSet;
    private SimulatedSession? _session;
    private long _messageId;
    private long _nextRunner = 600;
    private int _lastCapacity;

    internal GitHubSimulatorAddress Address { get; } = address ?? GitHubSimulatorAddress.Default;

    internal SimulatorRequest[] Requests => _requests.ToArray();
    internal Guid? SessionId { get { lock (_sync) { return _session?.Id; } } }
    internal long? RegisteredRunnerId { get { lock (_sync) { return _runners.Values.SingleOrDefault()?.Id; } } }
    internal long[] AcquiredRequests { get { lock (_sync) { return _acquired.Order().ToArray(); } } }
    internal int TotalAssignedJobs { get; set; }
    internal Func<HttpContext, IResult>? ReadExactSetResponse { get; set; }
    internal Func<DateTimeOffset>? CurrentStatisticsTime { get; set; }

    internal void SeedScaleSet(string name, IEnumerable<string> labels)
    {
        lock (_sync)
        {
            if (_scaleSet is not null)
            {
                throw new InvalidOperationException("The fixture scale set already exists.");
            }
            var payload = JsonSerializer.SerializeToElement(labels.Select(label => new { name = label, type = "System" }));
            _scaleSet = new SimulatedScaleSet(SetId, name, GroupId, payload);
        }
    }

    internal void FailNext(SimulatorOperation operation, SimulatorFault failure)
    {
        lock (_sync)
        {
            _faults.Add(operation, failure);
        }
    }

    internal long Offer(params long[] requestIds)
    {
        lock (_sync)
        {
            _available.UnionWith(requestIds);
            string body = JsonSerializer.Serialize(requestIds.Select(id =>
                new { messageType = "JobAvailable", runnerRequestId = id, requestLabels = DefaultLabels }));
            long id = _messageId++;
            _queue.Add(id, new SimulatedMessage(id, AsRequested, body, Statistics()));
            return id;
        }
    }

    internal void MarkRunnerListening(long runnerId)
    {
        lock (_sync)
        {
            _runners[runnerId] = _runners[runnerId] with { Listening = true };
        }
    }

    internal void ReplaceWithUnownedIdleRunner(long runnerId)
    {
        lock (_sync)
        {
            if (!_runners.Remove(runnerId))
            {
                throw new InvalidOperationException("The fixture runner to replace is missing.");
            }
            var foreign = new SimulatedRunner(_nextRunner++, SetId, "unowned-idle-runner", Assigned: false, Listening: true);
            _runners.Add(foreign.Id, foreign);
        }
    }

    internal void ForgetRunnerRegistration(long runnerId)
    {
        lock (_sync)
        {
            if (!_runners.Remove(runnerId))
            {
                throw new InvalidOperationException("The fixture runner to remove is missing.");
            }
        }
    }

    internal void AssignBeforeRunnerReportsStart(long runnerId)
    {
        lock (_sync)
        {
            _runners[runnerId] = _runners[runnerId] with { Assigned = true };
        }
    }

    internal long StartJob(long requestId, long runnerId)
    {
        lock (_sync)
        {
            var runner = _runners[runnerId];
            _runners[runnerId] = runner with { Assigned = true };
            long id = _messageId++;
            string body = JsonSerializer.Serialize(new[]
            {
                new { messageType = "JobStarted", runnerRequestId = requestId, runnerId, runnerName = runner.Name },
            });
            _queue.Add(id, new SimulatedMessage(id, AsRequested, body, new { totalAssignedJobs = TotalAssignedJobs }));
            return id;
        }
    }

    internal void Complete(long requestId, long? runnerId = null)
    {
        lock (_sync)
        {
            long id = _messageId++;
            string body;
            if (runnerId is long exact)
            {
                var runner = _runners[exact];
                _runners[exact] = runner with { Assigned = false };
                body = JsonSerializer.Serialize(new[]
                {
                    new { messageType = "JobCompleted", runnerRequestId = requestId, runnerId = exact, runnerName = runner.Name, result = "success" },
                });
            }
            else
            {
                body = JsonSerializer.Serialize(new[] { new { messageType = "JobCompleted", runnerRequestId = requestId, result = "success" } });
            }
            _queue.Add(id, new SimulatedMessage(id, AsRequested, body, new { totalAssignedJobs = TotalAssignedJobs }));
        }
    }

    internal void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (Address.Hosts.Contains(request.Host.Host, StringComparer.OrdinalIgnoreCase))
            {
                _requests.Enqueue(new SimulatorRequest(request.Method, request.Host.Host, request.Path.Value ?? "",
                    request.Protocol, request.Query["lastMessageId"].ToString(), request.Headers["X-ScaleSetMaxCapacity"].ToString()));
            }
            await next(context).ConfigureAwait(false);
        });
        app.MapPost("/app/installations/{id:long}/access_tokens", (HttpContext context) =>
            Bearer(context).Split('.').Length == 3
                ? Results.Json(new { token = AppAccessToken, expires_at = "2099-01-01T00:00:00Z" }, statusCode: 201)
                : Results.Unauthorized()).RequireHost(Address.ApiHosts);
        app.MapPost("/orgs/{owner}/actions/runners/registration-token", RegistrationTokenResult)
            .RequireHost(Address.ApiHosts);
        app.MapPost("/repos/{owner}/{repo}/actions/runners/registration-token", RegistrationTokenResult)
            .RequireHost(Address.ApiHosts);
        app.MapPost("/enterprises/{enterprise}/actions/runners/registration-token", RegistrationTokenResult)
            .RequireHost(Address.ApiHosts);
        app.MapGet("/orgs/{owner}/actions/runners/{id:long}", (long id, HttpContext context) =>
            ReadRunnerAvailability(context, id, requireApp: true)).RequireHost(Address.ApiHosts);
        app.MapGet("/repos/{owner}/{repo}/actions/runners/{id:long}", (long id, HttpContext context) =>
            ReadRunnerAvailability(context, id, requireApp: true)).RequireHost(Address.ApiHosts);
        app.MapGet("/enterprises/{enterprise}/actions/runners/{id:long}", (long id, HttpContext context) =>
            ReadRunnerAvailability(context, id, requireApp: false)).RequireHost(Address.ApiHosts);
        app.MapPost("/actions/runner-registration", (HttpContext context) =>
            context.Request.Headers.Authorization == "RemoteAuth " + RegistrationToken
                ? Results.Json(new { url = $"https://{Address.ServiceHost}{ServiceBase}", token = AdminToken })
                : Results.Unauthorized()).RequireHost(Address.ApiHosts);

        app.MapGet(ServiceBase + "/_apis/runtime/runnergroups/", (HttpContext context) =>
            Admin(context, () => ResultList(new { id = GroupId, name = groupName },
                context.Request.Query["groupName"].ToString() == groupName))).RequireHost(Address.ServiceHost);
        app.MapGet(Sets, (HttpContext context) => Admin(context, () =>
            ResultList(_scaleSet, _scaleSet is not null &&
                (context.Request.Query["name"].Count == 0 || context.Request.Query["name"] == _scaleSet.Name))))
            .RequireHost(Address.ServiceHost);
        app.MapGet(Sets + "/{id:long}", (long id, HttpContext context) => Admin(context, () =>
            _scaleSet is { } existing && existing.Id == id ? ReadExactSet(context, existing) : Results.NotFound()))
            .RequireHost(Address.ServiceHost);
        app.MapPost(Sets, (JsonElement input, HttpContext context) => Admin(context, () => Execute(SimulatorOperation.CreateScaleSet, () =>
        {
            if (_scaleSet is not null)
            {
                return Results.Conflict();
            }
            _scaleSet = new SimulatedScaleSet(SetId, input.GetProperty("name").GetString()!,
                input.GetProperty("runnerGroupId").GetInt64(), input.GetProperty("labels").Clone());
            return Results.Json(_scaleSet);
        }))).RequireHost(Address.ServiceHost);

        app.MapPost(Sessions, (HttpContext context) => Admin(context, () => Execute(SimulatorOperation.OpenSession, OpenSession)))
            .RequireHost(Address.ServiceHost);
        app.MapDelete(Sessions + "/{id:guid}", (Guid id, HttpContext context) => Admin(context,
            () => Execute(SimulatorOperation.CloseSession, () => CloseSession(id)))).RequireHost(Address.ServiceHost);
        app.MapGet("/messages/{id:guid}", (Guid id, HttpContext context) => Queue(context, id,
            () => Execute(SimulatorOperation.Poll, () => Poll(context)))).RequireHost(Address.QueueHost);
        app.MapDelete("/messages/{id:guid}/{messageId:long}", (Guid id, long messageId, HttpContext context) =>
            Queue(context, id, () => Execute(SimulatorOperation.Acknowledge, () =>
                _queue.Remove(messageId) ? Results.NoContent() : Results.NotFound()))).RequireHost(Address.QueueHost);
        app.MapPost(FormattableString.Invariant($"{Sets}/{SetId}/acquirejobs"), (long[] ids, HttpContext context) =>
        {
            lock (_sync)
            {
                if (_session is null || Bearer(context) != _session.Token)
                {
                    return Results.Unauthorized();
                }
                long[] granted = _lastCapacity == 0 ? [] : ids.Where(id => _available.Contains(id) && _acquired.Add(id)).ToArray();
                return Results.Json(new { count = granted.Length, value = granted });
            }
        }).RequireHost(Address.ServiceHost);

        app.MapPost(FormattableString.Invariant($"{Sets}/{SetId}/generatejitconfig"), (JsonElement input, HttpContext context) =>
            Admin(context, () => Execute(SimulatorOperation.GenerateJit, () =>
            {
                var runner = new SimulatedRunner(_nextRunner++, SetId, input.GetProperty("name").GetString()!, Assigned: false);
                _runners.Add(runner.Id, runner);
                return Results.Json(new { runner = RunnerPayload(runner), encodedJITConfig = Jit });
            }))).RequireHost(Address.ServiceHost);
        app.MapGet(Runners, (HttpContext context) => Admin(context, () =>
        {
            string name = context.Request.Query["agentName"].ToString();
            var runner = _runners.Values.SingleOrDefault(value => value.Name == name);
            return runner is null ? Results.Json(new { count = 0, value = Array.Empty<object>() }) : ResultList(RunnerPayload(runner), true);
        })).RequireHost(Address.ServiceHost);
        app.MapGet(Runners + "/{id:long}", (long id, HttpContext context) => Admin(context, () =>
            _runners.TryGetValue(id, out var runner) ? Results.Json(RunnerPayload(runner)) : AgentError(404, "AgentNotFoundException")))
            .RequireHost(Address.ServiceHost);
        app.MapDelete(Runners + "/{id:long}", (long id, HttpContext context) => Admin(context,
            () => Execute(SimulatorOperation.RemoveRunner, () => RemoveRunner(id)))).RequireHost(Address.ServiceHost);
    }

    private IResult Admin(HttpContext context, Func<IResult> result)
    {
        lock (_sync)
        {
            return Bearer(context) == AdminToken ? result() : Results.Unauthorized();
        }
    }

    private IResult ReadRunnerAvailability(HttpContext context, long id, bool requireApp)
    {
        bool authorized = requireApp ? Bearer(context) == AppAccessToken : Bearer(context) is FirstPat or NextPat;
        lock (_sync)
        {
            if (!authorized)
            {
                return Results.Unauthorized();
            }

            return _runners.TryGetValue(id, out var runner) ? Results.Json(new
            {
                runner.Id,
                runner.Name,
                status = runner.Listening ? "online" : "offline",
                busy = runner.Assigned,
            }) : Results.NotFound();
        }
    }

    private IResult Queue(HttpContext context, Guid id, Func<IResult> result)
    {
        lock (_sync)
        {
            return _session is { } session && session.Id == id && Bearer(context) == session.Token &&
                context.Request.Query["signature"] == "fixture-queue-signature"
                ? result()
                : Results.Unauthorized();
        }
    }

    private IResult OpenSession()
    {
        if (_scaleSet is null)
        {
            return Results.NotFound();
        }
        if (_session is not null)
        {
            return Results.Conflict();
        }
        var id = Guid.NewGuid();
        string token = $"fixture-session-{id:N}";
        _session = new SimulatedSession(id, token);
        _lastCapacity = 0;
        return Results.Json(new
        {
            sessionId = id,
            ownerName = "fixture-owner",
            messageQueueUrl = $"https://{Address.QueueHost}/messages/{id:D}?signature=fixture-queue-signature",
            messageQueueAccessToken = token,
            statistics = new { totalAssignedJobs = TotalAssignedJobs },
        });
    }

    private IResult CloseSession(Guid id)
    {
        if (_session?.Id != id)
        {
            return Results.NotFound();
        }
        _session = null;
        return Results.NoContent();
    }

    private IResult Poll(HttpContext context)
    {
        if (!int.TryParse(context.Request.Headers["X-ScaleSetMaxCapacity"], NumberStyles.None, CultureInfo.InvariantCulture, out _lastCapacity))
        {
            return Results.BadRequest();
        }
        string query = context.Request.Query["lastMessageId"].ToString();
        long cursor = -1;
        if (query.Length > 0 && !long.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out cursor))
        {
            return Results.BadRequest();
        }
        var message = _queue.Values.FirstOrDefault(item => item.MessageId > cursor);
        return message is null ? Results.StatusCode(202) : Results.Json(message);
    }

    private object Statistics() => new
    {
        totalAssignedJobs = TotalAssignedJobs,
        totalRegisteredRunners = _runners.Count,
        totalIdleRunners = _runners.Values.Count(runner => runner.Listening && !runner.Assigned),
        totalBusyRunners = _runners.Values.Count(runner => runner.Assigned),
    };

    private IResult ReadExactSet(HttpContext context, SimulatedScaleSet set)
    {
        if (ReadExactSetResponse is { } read)
        {
            return read(context);
        }

        if (CurrentStatisticsTime is { } time)
        {
            context.Response.Headers.Date = time().ToString("R", CultureInfo.InvariantCulture);
        }
        return Results.Json(new { set.Id, set.Name, set.RunnerGroupId, set.Labels, statistics = Statistics() });
    }

    private IResult RemoveRunner(long id)
    {
        if (!_runners.TryGetValue(id, out var runner))
        {
            return AgentError(404, "AgentNotFoundException");
        }
        if (runner.Assigned)
        {
            return AgentError(409, "JobStillRunningException");
        }
        _runners.Remove(id);
        return Results.NoContent();
    }

    private IResult Execute(SimulatorOperation operation, Func<IResult> transition)
    {
        if (!_faults.Remove(operation, out var fault))
        {
            return transition();
        }
        return fault switch
        {
            SimulatorFault.DropAfterCommit => Drop(transition()),
            SimulatorFault.BadRequest => Results.BadRequest(),
            SimulatorFault.NotFound => Results.NotFound(),
            SimulatorFault.RateLimited => new RateLimitResponse(429),
            SimulatorFault.ForbiddenRateLimit => new RateLimitResponse(403),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static IResult Drop(IResult result) => result is IStatusCodeHttpResult { StatusCode: null or 200 or 204 }
        ? new InterruptedResponse() : result;

    private static IResult RegistrationTokenResult(HttpContext context) =>
        Bearer(context) is AppAccessToken or FirstPat or NextPat
            ? Results.Json(new { token = RegistrationToken }, statusCode: 201)
            : Results.Unauthorized();

    private static string Bearer(HttpContext context)
    {
        string value = context.Request.Headers.Authorization.ToString();
        return value.StartsWith("Bearer ", StringComparison.Ordinal) ? value["Bearer ".Length..] : "";
    }

    private static IResult ResultList<T>(T value, bool included)
    {
        T[] items = included ? [value] : [];
        return Results.Json(new { count = items.Length, value = items });
    }

    private static IResult AgentError(int status, string name) =>
        Results.Json(new { typeName = "Microsoft.VisualStudio.Services.AgentPool." + name }, statusCode: status);

    private static object RunnerPayload(SimulatedRunner runner) =>
        new { id = runner.Id, runnerScaleSetId = runner.RunnerScaleSetId, name = runner.Name };

    private sealed record SimulatedScaleSet(long Id, string Name, long RunnerGroupId, JsonElement Labels);
    private sealed record SimulatedRunner(long Id, long RunnerScaleSetId, string Name, bool Assigned, bool Listening = false);
    private sealed record SimulatedSession(Guid Id, string Token);
    private sealed record SimulatedMessage(long MessageId, string MessageType, string Body, object Statistics);

    private sealed class RateLimitResponse(int status) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = "3";
            httpContext.Response.Headers["X-RateLimit-Remaining"] = "0";
            await Results.Json(new { message = "fixture request budget exhausted" }, statusCode: status).ExecuteAsync(httpContext)
                .ConfigureAwait(false);
        }
    }

    private sealed class InterruptedResponse : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = 200;
            httpContext.Response.ContentType = "application/json";
            httpContext.Response.ContentLength = 128;
            await httpContext.Response.Body.WriteAsync("{"u8.ToArray(), httpContext.RequestAborted).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
            httpContext.Abort();
        }
    }
}
