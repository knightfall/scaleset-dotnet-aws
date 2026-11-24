using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Handoff;

namespace Knightfall.ScaleSet.Aws.DynamoDb;

public enum BootstrapOfferStatus
{
    NotAvailable = 0,
    Consumed = 1,
    Indeterminate = 2,
    AlreadyConsumed = 3,
}

public sealed record BootstrapOfferReceipt(BootstrapOfferStatus Status, JitOfferEnvelope? Offer = null)
{
    /// <summary>Indicates that the exact decoded offer expired while its conditional deletion was in flight.</summary>
    public bool Expired { get; init; }
}

/// <summary>Performs only the HELLO put and one-shot OFFER delete allowed to the runner's own bootstrap principal.</summary>
public sealed class DynamoDbBootstrapOfferClient
{
    private const int ReadyToConsume = 0;
    private const int InFlight = 1;
    private const int Consumed = 2;
    private const int Indeterminate = 3;

    private readonly IAmazonDynamoDB _client;
    private readonly string _tableName;
    private readonly string _awsUserId;
    private readonly string _resourceArn;
    private readonly TimeProvider _clock;
    private int _consumeState;

    public DynamoDbBootstrapOfferClient(IAmazonDynamoDB client, string tableName, string awsUserId,
        string resourceArn, TimeProvider timeProvider)
    {
        BootstrapItems.RequireClient(client, tableName);
        BootstrapItems.RequirePrincipal(awsUserId, resourceArn);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _client = client;
        _tableName = tableName;
        _awsUserId = awsUserId;
        _resourceArn = resourceArn;
        _clock = timeProvider;
    }

    public async Task<LifecycleWriteOutcome> RegisterHelloAsync(JitHello hello, CancellationToken cancellationToken)
    {
        var key = LifecycleKey.Hello(_awsUserId);
        var item = BootstrapItems.Key(key);
        item.Add("awsUserId", BootstrapItems.String(_awsUserId));
        item.Add("helloSha256", BootstrapItems.String(BootstrapItems.HelloDigest(hello)));
        item.Add("protocolVersion", BootstrapItems.Number(hello.ProtocolVersion));
        item.Add("payload", BootstrapItems.String(BootstrapItems.Serialize(hello)));
        return await BootstrapItems.PutAsync(_client, new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(PK)",
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Only a known receipt contains ciphertext. An indeterminate result must not be retried.</summary>
    public async Task<BootstrapOfferReceipt> TryConsumeOfferAsync(JitHello hello, CancellationToken cancellationToken)
    {
        string helloDigest = BootstrapItems.HelloDigest(hello);
        var key = LifecycleKey.Offer(_awsUserId);
        cancellationToken.ThrowIfCancellationRequested();
        using var telemetry = DynamoDbDiagnostics.StartHandoff("offer.consume", _clock);
        int prior = Interlocked.CompareExchange(ref _consumeState, InFlight, ReadyToConsume);
        if (prior != ReadyToConsume)
        {
            return telemetry.Complete(new BootstrapOfferReceipt(
                prior == Consumed ? BootstrapOfferStatus.AlreadyConsumed : BootstrapOfferStatus.Indeterminate));
        }

        int finish = Indeterminate;
        try
        {
            var response = await _client.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = _tableName,
                Key = BootstrapItems.Key(key),
                ReturnValues = ReturnValue.ALL_OLD,
                ConditionExpression = "awsUserId = :principal AND resourceArn = :resource AND helloSha256 = :hello AND " +
                    "protocolVersion = :protocol AND offerExpiresAt > :now",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":principal"] = BootstrapItems.String(_awsUserId),
                    [":resource"] = BootstrapItems.String(_resourceArn),
                    [":hello"] = BootstrapItems.String(helloDigest),
                    [":protocol"] = BootstrapItems.Number(hello.ProtocolVersion),
                    [":now"] = BootstrapItems.Number(_clock.GetUtcNow().ToUnixTimeMilliseconds()),
                },
            }, cancellationToken).ConfigureAwait(false);
            if (response.HttpStatusCode != HttpStatusCode.OK || response.Attributes is not { Count: > 0 } item)
            {
                return telemetry.Complete(new BootstrapOfferReceipt(BootstrapOfferStatus.Indeterminate));
            }
            var offer = BootstrapItems.DecodeOffer(key, item, helloDigest);
            if (offer.Binding.Identity.ResourceArn != _resourceArn ||
                !offer.Binding.BootNonce.AsSpan().SequenceEqual(hello.BootNonce.AsSpan()))
            {
                return telemetry.Complete(new BootstrapOfferReceipt(BootstrapOfferStatus.Indeterminate));
            }
            if (offer.Binding.ExpiresAtUnixMilliseconds <= _clock.GetUtcNow().ToUnixTimeMilliseconds())
            {
                return telemetry.Complete(new BootstrapOfferReceipt(BootstrapOfferStatus.Indeterminate) { Expired = true });
            }
            finish = Consumed;
            return telemetry.Complete(new BootstrapOfferReceipt(BootstrapOfferStatus.Consumed, offer));
        }
        catch (ConditionalCheckFailedException)
        {
            finish = ReadyToConsume;
            return telemetry.Complete(new BootstrapOfferReceipt(BootstrapOfferStatus.NotAvailable));
        }
        catch (Exception error) when (BootstrapItems.IsIndeterminate(error) ||
                                      error is InvalidDataException or CryptographicException or JsonException)
        {
            return telemetry.Complete(new BootstrapOfferReceipt(BootstrapOfferStatus.Indeterminate));
        }
        catch (AmazonServiceException)
        {
            throw new IOException("The bootstrap offer delete was rejected.");
        }
        finally
        {
            Volatile.Write(ref _consumeState, finish);
        }
    }
}
