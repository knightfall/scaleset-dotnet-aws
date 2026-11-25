using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;

namespace Knightfall.ScaleSet.Aws.Lifecycle.Tests;

internal sealed class MiniStackTable : IDisposable
{
    internal static readonly DateTimeOffset Now = new(2050, 1, 2, 3, 4, 5, TimeSpan.Zero);
    internal const string FirstTask = "arn:aws:ecs:us-east-1:000000000000:task/controller/00000000000000000000000000000001";

    internal readonly AmazonDynamoDBClient Client;
    internal readonly MiniStackWire Wire = new();
    internal readonly string Name = "lifecycle-" + Guid.NewGuid().ToString("N");
    internal readonly DeploymentLeaseFence Lease = new("test", "controller-1", 1, ControllerLease.CurrentWriterProtocol, FirstTask);

    private MiniStackTable()
    {
        var endpoint = Environment.GetEnvironmentVariable("SCALESET_MINISTACK_ENDPOINT");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            uri.Host != "127.0.0.1" || uri.IsDefaultPort || uri.AbsolutePath != "/" || uri.Query.Length != 0)
        {
            throw new InvalidOperationException("Use make check-lifecycle to provide a loopback MiniStack endpoint.");
        }

        Client = new AmazonDynamoDBClient(new BasicAWSCredentials("000000000000", "test-only-secret"),
            new AmazonDynamoDBConfig
            {
                ServiceURL = uri.AbsoluteUri,
                AuthenticationRegion = "us-east-1",
                Timeout = TimeSpan.FromSeconds(15),
                MaxErrorRetry = 0,
                MaxStaleConnectionRetries = 0,
                DisableClockSkewCorrection = true,
                HttpClientFactory = new MiniStackClientFactory(Wire),
            });
    }

    internal DynamoDbLifecycleStore CreateStore(DateTimeOffset? now = null) => new(Client, Name, new FixtureClock(now ?? Now));

    internal LifecycleProfile Profile(string name) => new(Lease.Deployment, name);

    internal static async Task<MiniStackTable> CreateAsync(CancellationToken cancellationToken)
    {
        var table = new MiniStackTable();
        try
        {
            await table.Client.CreateTableAsync(new CreateTableRequest
            {
                TableName = table.Name,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                KeySchema = [new("PK", KeyType.HASH), new("SK", KeyType.RANGE)],
                AttributeDefinitions =
                [
                    new("PK", ScalarAttributeType.S),
                    new("SK", ScalarAttributeType.S),
                    new("dueShard", ScalarAttributeType.S),
                    new("dueAtMillis", ScalarAttributeType.N),
                ],
                GlobalSecondaryIndexes =
                [
                    new()
                    {
                        IndexName = "due",
                        KeySchema = [new("dueShard", KeyType.HASH), new("dueAtMillis", KeyType.RANGE)],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                ],
            }, cancellationToken);

            await table.SetLeaseAsync(table.Lease, Now.AddHours(1), cancellationToken);
            table.Wire.Clear();
            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }

    internal async Task SetLeaseAsync(DeploymentLeaseFence fence, DateTimeOffset validUntil, CancellationToken cancellationToken)
    {
        var leaseItem = Key(LifecycleKey.Lease(fence.Deployment));
        leaseItem["deployment"] = new AttributeValue(fence.Deployment);
        leaseItem["revision"] = new AttributeValue { N = "1" };
        leaseItem["transitionId"] = new AttributeValue(Guid.NewGuid().ToString("D"));
        leaseItem["ownerId"] = new AttributeValue(fence.OwnerId);
        leaseItem["ownerTaskArn"] = new AttributeValue(fence.OwnerTaskArn);
        leaseItem["leaseMode"] = new AttributeValue("active");
        leaseItem["epoch"] = new AttributeValue { N = FormattableString.Invariant($"{fence.Epoch}") };
        leaseItem["writerProtocol"] = new AttributeValue { N = FormattableString.Invariant($"{fence.WriterProtocol}") };
        leaseItem["validUntil"] = new AttributeValue { N = FormattableString.Invariant($"{validUntil.ToUnixTimeMilliseconds()}") };
        await Client.PutItemAsync(Name, leaseItem, cancellationToken);
    }

    internal async Task<Dictionary<string, AttributeValue>> ReadAsync(LifecycleKey key, CancellationToken cancellationToken)
    {
        var response = await Client.GetItemAsync(new GetItemRequest
        {
            TableName = Name,
            Key = Key(key),
            ConsistentRead = true,
        }, cancellationToken);
        return response.Item ?? [];
    }

    internal async Task SeedEffectsAsync(
        LifecycleProfile profile, int count, IReadOnlySet<int> unfinished, CancellationToken cancellationToken)
    {
        string transition = Guid.NewGuid().ToString("D");
        var rows = Enumerable.Range(0, count).Select(index =>
        {
            var item = Key(LifecycleKey.Operation(profile, RetainedIntent(index)));
            item["deployment"] = new AttributeValue(profile.Deployment);
            item["revision"] = new AttributeValue { N = "1" };
            item["transitionId"] = new AttributeValue(transition);
            item["effectState"] = new AttributeValue(unfinished.Contains(index) ? "uncertain" : "settled");
            item["payload"] = new AttributeValue("{}");
            return new WriteRequest { PutRequest = new PutRequest { Item = item } };
        });

        foreach (var batch in rows.Chunk(25))
        {
            Dictionary<string, List<WriteRequest>> pending = new(StringComparer.Ordinal) { [Name] = [.. batch] };
            for (int attempt = 0; pending.Count != 0; attempt++)
            {
                if (attempt == 3)
                {
                    throw new InvalidOperationException("MiniStack did not complete the local seed batch.");
                }
                var response = await Client.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = pending }, cancellationToken);
                pending = response.UnprocessedItems ?? [];
            }
        }
    }

    internal static string RetainedIntent(int index) => FormattableString.Invariant($"retained-{index:D5}");

    internal static Dictionary<string, AttributeValue> Key(LifecycleKey key) => new(StringComparer.Ordinal)
    {
        ["PK"] = new AttributeValue(key.PartitionKey),
        ["SK"] = new AttributeValue(key.SortKey),
    };

    public void Dispose()
    {
        Client.Dispose();
        Wire.Dispose();
    }

    private sealed class FixtureClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MiniStackClientFactory(MiniStackWire wire) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(wire, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}

internal sealed record MiniStackCall(string Operation, JsonElement Request)
{
    internal bool IsDueQuery => Operation == "Query" &&
        Request.TryGetProperty("IndexName", out var index) && index.GetString() == "due";

    internal string? QueryPrefix => Operation == "Query" && Request.GetProperty("ExpressionAttributeValues")
        .TryGetProperty(":prefix", out var prefix)
            ? prefix.GetProperty("S").GetString()
            : null;
}

internal sealed class MiniStackWire : DelegatingHandler
{
    private readonly ConcurrentQueue<MiniStackCall> _requests = new();
    private int _dropTransactionBeforeSend;
    private int _dropSuccessfulTransaction;
    private int _dropSuccessfulBootstrapDelete;
    private LifecycleKey? _dropSuccessfulStatusWrite;
    private TransactionBarrier? _transactionBarrier;
    private PausedRequest? _pausedRead;
    private PausedRequest? _pausedWrite;
    private PausedRequest? _pausedSuccessfulWrite;
    private PausedRequest? _pausedSuccessfulBootstrapDelete;
    private StorageFailure? _failedGetItem;
    private StorageFailure? _failedTransactWrite;

    internal IReadOnlyList<MiniStackCall> Requests => _requests.ToArray();

    internal string? DueQueryResponse { get; set; }

    internal MiniStackWire()
        : base(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
    }

    internal void Clear() => _requests.Clear();

    internal void DropNextTransactionBeforeSend() => Interlocked.Exchange(ref _dropTransactionBeforeSend, 1);

    internal void DropNextSuccessfulTransactionResponse() => Interlocked.Exchange(ref _dropSuccessfulTransaction, 1);

    internal void DropNextSuccessfulBootstrapDeleteResponse() => Interlocked.Exchange(ref _dropSuccessfulBootstrapDelete, 1);

    internal void DropNextSuccessfulStatusWriteResponse(LifecycleKey key) => Interlocked.Exchange(ref _dropSuccessfulStatusWrite, key);

    internal void HoldNextTwoTransactions() => _transactionBarrier = new TransactionBarrier();

    internal void FailNextGetItem(LifecycleKey key, HttpStatusCode? status, string? errorCode) =>
        Interlocked.Exchange(ref _failedGetItem, new StorageFailure(key, status, errorCode));

    internal void FailNextTransactWrite(LifecycleKey key, string errorCode) =>
        Interlocked.Exchange(ref _failedTransactWrite, new StorageFailure(key, HttpStatusCode.BadRequest, errorCode));

    internal PausedRequest PauseNextRead(LifecycleKey key)
    {
        var pause = new PausedRequest(key);
        _pausedRead = pause;
        return pause;
    }

    internal PausedRequest PauseNextTransactionWrite(LifecycleKey key)
    {
        var pause = new PausedRequest(key);
        _pausedWrite = pause;
        return pause;
    }

    internal PausedRequest PauseAfterNextSuccessfulTransactionWrite(LifecycleKey key)
    {
        var pause = new PausedRequest(key);
        _pausedSuccessfulWrite = pause;
        return pause;
    }

    internal PausedRequest PauseAfterNextSuccessfulBootstrapDelete(LifecycleKey key)
    {
        var pause = new PausedRequest(key);
        _pausedSuccessfulBootstrapDelete = pause;
        return pause;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { Scheme: "http", Host: "127.0.0.1" })
        {
            throw new InvalidOperationException("The fixture only permits loopback requests.");
        }
        var target = request.Headers.GetValues("X-Amz-Target").Single();
        string operation = target[(target.LastIndexOf('.') + 1)..];
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var call = new MiniStackCall(operation, document.RootElement.Clone());
        _requests.Enqueue(call);

        if (operation == "GetItem" && _failedGetItem is { } fault &&
            MatchesKey(call.Request.GetProperty("Key"), fault.Key) &&
            ReferenceEquals(Interlocked.CompareExchange(ref _failedGetItem, null, fault), fault))
        {
            return FailureResponse(fault);
        }
        if (operation == "TransactWriteItems" && _failedTransactWrite is { } writeFault &&
            call.Request.GetProperty("TransactItems").EnumerateArray().Any(action =>
                action.TryGetProperty("Put", out var put) && MatchesKey(put.GetProperty("Item"), writeFault.Key)) &&
            ReferenceEquals(Interlocked.CompareExchange(ref _failedTransactWrite, null, writeFault), writeFault))
        {
            return FailureResponse(writeFault);
        }
        if (operation == "TransactWriteItems" && Interlocked.Exchange(ref _dropTransactionBeforeSend, 0) == 1)
        {
            throw new HttpRequestException("Lost request before MiniStack received the transaction.",
                new SocketException((int)SocketError.ConnectionReset));
        }
        if (operation == "TransactWriteItems" && _transactionBarrier is { } barrier)
        {
            await barrier.WaitAsync(cancellationToken);
        }
        if (operation == "TransactWriteItems" && _pausedWrite is { } writePause &&
            MatchesTransactionPut(call, writePause) &&
            Interlocked.CompareExchange(ref _pausedWrite, null, writePause) == writePause)
        {
            await writePause.AwaitResumeAsync(cancellationToken);
        }
        var actual = await base.SendAsync(request, cancellationToken);
        if (operation == "TransactWriteItems" && actual.IsSuccessStatusCode && _pausedSuccessfulWrite is { } afterWrite &&
            MatchesTransactionPut(call, afterWrite) &&
            Interlocked.CompareExchange(ref _pausedSuccessfulWrite, null, afterWrite) == afterWrite)
        {
            await afterWrite.AwaitResumeAsync(cancellationToken);
        }
        if (operation == "DeleteItem" && actual.IsSuccessStatusCode && _pausedSuccessfulBootstrapDelete is { } afterDelete &&
            afterDelete.Matches(call.Request.GetProperty("Key")) &&
            Interlocked.CompareExchange(ref _pausedSuccessfulBootstrapDelete, null, afterDelete) == afterDelete)
        {
            await afterDelete.AwaitResumeAsync(cancellationToken);
        }
        if (operation == "GetItem" && _pausedRead is { } pause &&
            pause.Matches(call.Request.GetProperty("Key")) &&
            Interlocked.CompareExchange(ref _pausedRead, null, pause) == pause)
        {
            await pause.AwaitResumeAsync(cancellationToken);
        }
        if (operation == "TransactWriteItems" && actual.IsSuccessStatusCode &&
            Interlocked.Exchange(ref _dropSuccessfulTransaction, 0) == 1)
        {
            actual.Dispose();
            throw new HttpRequestException("Lost response after MiniStack committed the transaction.",
                new SocketException((int)SocketError.ConnectionReset));
        }
        if (operation == "DeleteItem" && actual.IsSuccessStatusCode &&
            Interlocked.Exchange(ref _dropSuccessfulBootstrapDelete, 0) == 1)
        {
            actual.Dispose();
            throw new HttpRequestException("Lost response after MiniStack deleted the bootstrap offer.",
                new SocketException((int)SocketError.ConnectionReset));
        }
        if (operation == "PutItem" && actual.IsSuccessStatusCode && _dropSuccessfulStatusWrite is { } statusKey &&
            MatchesKey(call.Request.GetProperty("Item"), statusKey) &&
            ReferenceEquals(Interlocked.CompareExchange(ref _dropSuccessfulStatusWrite, null, statusKey), statusKey))
        {
            actual.Dispose();
            throw new HttpRequestException("Lost response after MiniStack committed the status.",
                new SocketException((int)SocketError.ConnectionReset));
        }

        if (call.IsDueQuery && actual.IsSuccessStatusCode && DueQueryResponse is { } body)
        {
            actual.Dispose();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-amz-json-1.0"),
            };
        }
        return actual;
    }

    private static bool MatchesTransactionPut(MiniStackCall call, PausedRequest pause) =>
        call.Request.GetProperty("TransactItems").EnumerateArray().Any(action =>
            action.TryGetProperty("Put", out var put) && pause.Matches(put.GetProperty("Item")));

    private static bool MatchesKey(JsonElement item, LifecycleKey key) =>
        item.GetProperty("PK").GetProperty("S").GetString() == key.PartitionKey &&
        item.GetProperty("SK").GetProperty("S").GetString() == key.SortKey;

    private static HttpResponseMessage FailureResponse(StorageFailure fault)
    {
        if (fault.Status is not { } status)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "synthetic storage diagnostic",
                new SocketException((int)SocketError.ConnectionReset));
        }
        string failurePayload = JsonSerializer.Serialize(new
        {
            __type = "com.amazonaws.dynamodb.v20120810#" + fault.ErrorCode,
            message = "synthetic storage diagnostic",
        });
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(failurePayload, Encoding.UTF8, "application/x-amz-json-1.0"),
        };
    }

    private sealed record StorageFailure(LifecycleKey Key, HttpStatusCode? Status, string? ErrorCode);

    internal sealed class PausedRequest(LifecycleKey key)
    {
        private readonly TaskCompletionSource _captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Captured => _captured.Task;

        internal bool Matches(JsonElement item) => MatchesKey(item, key);

        internal async Task AwaitResumeAsync(CancellationToken cancellationToken)
        {
            _captured.TrySetResult();
            await _resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        internal void Resume() => _resume.TrySetResult();
    }

    private sealed class TransactionBarrier
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        internal Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _release.SetResult();
            }
            return _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }
}
