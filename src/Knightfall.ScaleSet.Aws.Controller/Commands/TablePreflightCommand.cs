using System.Net;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Knightfall.ScaleSet.Aws.Core.Configuration;

namespace Knightfall.ScaleSet.Aws.Controller.Commands;

public enum TablePreflightStatus
{
    Verified = 0,
    AccountMismatch = 1,
    TableMismatch = 2,
    SchemaMismatch = 3,
    TimeToLiveUnverified = 4,
    PointInTimeRecoveryUnverified = 5,
}

public static class TablePreflightCommand
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        var artifact = PreflightConfiguration.Parse(args, "table");
        if (artifact is null)
        {
            await stderr.WriteLineAsync("Use preflight table --config <runtime.json> --sha256 <hex>.".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return 2;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), TimeProvider.System);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        try
        {
            var configuration = await artifact.LoadAsync(token).ConfigureAwait(false);
            var dynamoOptions = Config(new AmazonDynamoDBConfig(), configuration.Aws.Region);
            var credentials = await DefaultAWSCredentialsIdentityResolver.GetCredentialsAsync(dynamoOptions).WaitAsync(token).ConfigureAwait(false);
            using var dynamo = new AmazonDynamoDBClient(credentials, dynamoOptions);
            using var sts = new AmazonSecurityTokenServiceClient(credentials, Config(new AmazonSecurityTokenServiceConfig(), configuration.Aws.Region));
            var result = await VerifyAsync(configuration, sts, dynamo, token).ConfigureAwait(false);
            if (result == TablePreflightStatus.Verified)
            {
                await stdout.WriteLineAsync("Tracking table preflight verified.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return 0;
            }

            await stderr.WriteLineAsync($"Tracking table preflight failed ({result}).".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("Tracking table preflight exceeded its read deadline.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }
        catch (Exception error) when (error is AmazonClientException or AmazonServiceException or HttpRequestException or IOException or
                                      UnauthorizedAccessException or RuntimeConfigurationException or TimeoutException or ArgumentException)
        {
            await stderr.WriteLineAsync("Tracking table preflight could not verify the deployed table.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    public static async Task<TablePreflightStatus> VerifyAsync(RuntimeConfiguration configuration, IAmazonSecurityTokenService sts,
        IAmazonDynamoDB dynamo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sts);
        ArgumentNullException.ThrowIfNull(dynamo);

        string account = configuration.Aws.Account;
        string region = configuration.Aws.Region;
        string partition = RegionEndpoint.GetBySystemName(region).PartitionName;
        var caller = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken).ConfigureAwait(false);
        if (caller is not { HttpStatusCode: HttpStatusCode.OK } || caller.Account != account ||
            !Arn.TryParse(caller.Arn, out var callerArn) || callerArn.Partition != partition || callerArn.AccountId != account)
        {
            return TablePreflightStatus.AccountMismatch;
        }

        string tableName = configuration.TrackingTable.Name;
        var response = await dynamo.DescribeTableAsync(new DescribeTableRequest { TableName = tableName }, cancellationToken).ConfigureAwait(false);
        if (response is not { HttpStatusCode: HttpStatusCode.OK, Table: { } table } || table.TableName != tableName ||
            table.TableArn != $"arn:{partition}:dynamodb:{region}:{account}:table/{tableName}" || table.TableStatus != TableStatus.ACTIVE)
        {
            return TablePreflightStatus.TableMismatch;
        }

        if (!ValidSchema(table))
        {
            return TablePreflightStatus.SchemaMismatch;
        }

        var ttl = await dynamo.DescribeTimeToLiveAsync(new DescribeTimeToLiveRequest { TableName = tableName }, cancellationToken).ConfigureAwait(false);
        if (ttl is not { HttpStatusCode: HttpStatusCode.OK, TimeToLiveDescription: { AttributeName: "expiresAt" } expiration } ||
            expiration.TimeToLiveStatus != TimeToLiveStatus.ENABLED)
        {
            return TablePreflightStatus.TimeToLiveUnverified;
        }

        var backup = await dynamo.DescribeContinuousBackupsAsync(new DescribeContinuousBackupsRequest { TableName = tableName }, cancellationToken)
            .ConfigureAwait(false);
        return backup is { HttpStatusCode: HttpStatusCode.OK, ContinuousBackupsDescription: { } settings } &&
            settings.ContinuousBackupsStatus == ContinuousBackupsStatus.ENABLED &&
            settings.PointInTimeRecoveryDescription?.PointInTimeRecoveryStatus == PointInTimeRecoveryStatus.ENABLED
            ? TablePreflightStatus.Verified
            : TablePreflightStatus.PointInTimeRecoveryUnverified;
    }

    private static bool ValidSchema(TableDescription table)
    {
        // DescribeTable omits SSEDescription for the default AWS-owned encryption key.
        if ((table.SSEDescription is { } encryption && encryption.Status != SSEStatus.ENABLED) ||
            !MatchesKeys(table.KeySchema, "PK", "SK") ||
            table.AttributeDefinitions is not { } attributes ||
            !MatchesAttribute(attributes, "PK", ScalarAttributeType.S) || !MatchesAttribute(attributes, "SK", ScalarAttributeType.S) ||
            !MatchesAttribute(attributes, "dueShard", ScalarAttributeType.S) || !MatchesAttribute(attributes, "dueAtMillis", ScalarAttributeType.N))
        {
            return false;
        }

        var due = table.GlobalSecondaryIndexes?.Where(index => index.IndexName == "due").ToArray();
        return due is [{ } index] && index.IndexStatus == IndexStatus.ACTIVE && index.Backfilling is not true &&
            index.Projection?.ProjectionType == ProjectionType.KEYS_ONLY && MatchesKeys(index.KeySchema, "dueShard", "dueAtMillis");
    }

    private static bool MatchesKeys(List<KeySchemaElement>? keys, string partition, string sort) =>
        keys is { Count: 2 } && keys.Any(key => key.AttributeName == partition && key.KeyType == KeyType.HASH) &&
        keys.Any(key => key.AttributeName == sort && key.KeyType == KeyType.RANGE);

    private static bool MatchesAttribute(List<AttributeDefinition> attributes, string name, ScalarAttributeType type) =>
        attributes.Where(attribute => attribute.AttributeName == name).ToArray() is [{ } attribute] && attribute.AttributeType == type;

    private static T Config<T>(T configuration, string region) where T : ClientConfig
    {
        configuration.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        configuration.IgnoreConfiguredEndpointUrls = true;
        configuration.AllowAutoRedirect = false;
        configuration.MaxErrorRetry = 0;
        configuration.MaxStaleConnectionRetries = 0;
        configuration.DisableClockSkewCorrection = true;
        return configuration;
    }
}
