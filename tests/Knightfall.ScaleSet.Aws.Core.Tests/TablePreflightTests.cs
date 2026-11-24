using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Knightfall.ScaleSet.Aws.Controller.Commands;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class TablePreflightTests
{
    private const string Account = "111122223333";
    private const string Region = "ap-southeast-2";
    private const string Name = "existing-runner-lifecycle";
    private const string TableArn = "arn:aws:dynamodb:" + Region + ":" + Account + ":table/" + Name;
    private const string DescribeTable = $$$"""
        {"Table":{"TableName":"{{{Name}}}","TableArn":"{{{TableArn}}}","TableStatus":"ACTIVE",
         "KeySchema":[{"AttributeName":"PK","KeyType":"HASH"},{"AttributeName":"SK","KeyType":"RANGE"}],
         "AttributeDefinitions":[{"AttributeName":"PK","AttributeType":"S"},{"AttributeName":"SK","AttributeType":"S"},
                                 {"AttributeName":"dueShard","AttributeType":"S"},{"AttributeName":"dueAtMillis","AttributeType":"N"}],
         "GlobalSecondaryIndexes":[{"IndexName":"due","IndexStatus":"ACTIVE","Projection":{"ProjectionType":"KEYS_ONLY"},
                    "KeySchema":[{"AttributeName":"dueShard","KeyType":"HASH"},{"AttributeName":"dueAtMillis","KeyType":"RANGE"}]}]}}
        """;
    private const string Ttl = """{"TimeToLiveDescription":{"AttributeName":"expiresAt","TimeToLiveStatus":"ENABLED"}}""";
    private const string Backups = """{"ContinuousBackupsDescription":{"ContinuousBackupsStatus":"ENABLED","PointInTimeRecoveryDescription":{"PointInTimeRecoveryStatus":"ENABLED"}}}""";

    [Theory]
    [InlineData("valid", TablePreflightStatus.Verified, 4)]
    [InlineData("foreign-caller", TablePreflightStatus.AccountMismatch, 1)]
    [InlineData("foreign-table", TablePreflightStatus.TableMismatch, 2)]
    [InlineData("wrong-region", TablePreflightStatus.TableMismatch, 2)]
    [InlineData("table-creating", TablePreflightStatus.TableMismatch, 2)]
    [InlineData("wrong-key", TablePreflightStatus.SchemaMismatch, 2)]
    [InlineData("wrong-attribute-type", TablePreflightStatus.SchemaMismatch, 2)]
    [InlineData("wrong-index-projection", TablePreflightStatus.SchemaMismatch, 2)]
    [InlineData("no-index", TablePreflightStatus.SchemaMismatch, 2)]
    [InlineData("ttl-disabled", TablePreflightStatus.TimeToLiveUnverified, 3)]
    [InlineData("wrong-ttl-key", TablePreflightStatus.TimeToLiveUnverified, 3)]
    [InlineData("pitr-disabled", TablePreflightStatus.PointInTimeRecoveryUnverified, 4)]
    public async Task TablePreflightVerifiesExactAccountAndEnvironmentUsingOnlyReads(string defect, TablePreflightStatus expected, int requestCount)
    {
        var token = TestContext.Current.CancellationToken;
        string fixture = Path.Combine(AppContext.BaseDirectory, "ConfigFixtures", "runtime", "fargate-only.json");
        var config = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(fixture, token));
        using var wire = new Wire(defect);
        var credential = new BasicAWSCredentials("fixture-access", "fixture-only-secret");
        using var sts = new AmazonSecurityTokenServiceClient(credential, ClientConfig(new AmazonSecurityTokenServiceConfig(), wire));
        using var dynamo = new AmazonDynamoDBClient(credential, ClientConfig(new AmazonDynamoDBConfig(), wire));

        var result = await TablePreflightCommand.VerifyAsync(config, sts, dynamo, token);

        Assert.Equal(expected, result);
        string[] allowed = ["GetCallerIdentity", "DescribeTable", "DescribeTimeToLive", "DescribeContinuousBackups"];
        Assert.Equal(allowed[..requestCount], wire.Operations);
    }

    private static T ClientConfig<T>(T config, Wire wire) where T : ClientConfig
    {
        config.ServiceURL = "https://aws.fixture.invalid/";
        config.AuthenticationRegion = Region;
        config.MaxErrorRetry = 0;
        config.MaxStaleConnectionRetries = 0;
        config.DisableClockSkewCorrection = true;
        config.HttpClientFactory = new FixtureFactory(wire);
        return config;
    }

    private sealed class FixtureFactory(Wire wire) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(wire, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    }

    private sealed class Wire(string defect) : HttpMessageHandler
    {
        internal List<string> Operations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            string document = await Assert.IsAssignableFrom<HttpContent>(request.Content).ReadAsStringAsync(cancellationToken);
            if (!request.Headers.TryGetValues("X-Amz-Target", out var target))
            {
                Assert.Contains("Action=GetCallerIdentity", document, StringComparison.Ordinal);
                Operations.Add("GetCallerIdentity");
                string account = defect == "foreign-caller" ? "999999999999" : Account;
                return Reply($"""
                    <GetCallerIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
                      <GetCallerIdentityResult><Account>{account}</Account><Arn>arn:aws:iam::{account}:user/fixture</Arn>
                      <UserId>AIDAFIXTURE</UserId></GetCallerIdentityResult>
                    </GetCallerIdentityResponse>
                    """, "text/xml");
            }

            using var body = JsonDocument.Parse(document);
            Assert.Equal(Name, body.RootElement.GetProperty("TableName").GetString());
            string operation = Assert.Single(target)["DynamoDB_20120810.".Length..];
            Operations.Add(operation);
            return Reply(operation switch
            {
                "DescribeTable" => defect switch
                {
                    "foreign-table" => DescribeTable.Replace(Account, "999999999999", StringComparison.Ordinal),
                    "wrong-region" => DescribeTable.Replace(Region, "us-east-1", StringComparison.Ordinal),
                    "table-creating" => DescribeTable.Replace("ACTIVE", "CREATING", StringComparison.Ordinal),
                    "wrong-key" => DescribeTable.Replace("\"SK\"", "\"Sort\"", StringComparison.Ordinal),
                    "wrong-attribute-type" => DescribeTable.Replace("\"N\"", "\"S\"", StringComparison.Ordinal),
                    "wrong-index-projection" => DescribeTable.Replace("KEYS_ONLY", "ALL", StringComparison.Ordinal),
                    "no-index" => DescribeTable.Replace("\"IndexName\":\"due\"", "\"IndexName\":\"other\"", StringComparison.Ordinal),
                    _ => DescribeTable,
                },
                "DescribeTimeToLive" => defect switch
                {
                    "ttl-disabled" => Ttl.Replace("ENABLED", "DISABLED", StringComparison.Ordinal),
                    "wrong-ttl-key" => Ttl.Replace("expiresAt", "ttl", StringComparison.Ordinal),
                    _ => Ttl,
                },
                "DescribeContinuousBackups" => defect == "pitr-disabled"
                    ? Backups.Replace("\"PointInTimeRecoveryStatus\":\"ENABLED\"", "\"PointInTimeRecoveryStatus\":\"DISABLED\"", StringComparison.Ordinal)
                    : Backups,
                _ => throw new InvalidOperationException("Unexpected AWS operation in a read-only preflight."),
            }, "application/x-amz-json-1.0");
        }

        private static HttpResponseMessage Reply(string body, string mediaType) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
        };
    }
}
