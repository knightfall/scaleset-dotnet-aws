using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;

public sealed class AwsSupportingServicesWireTests
{
    private const string RoleArn = "arn:aws:iam::123456789012:role/runner-bootstrap";
    private const string RoleId = "AROAEXAMPLE0123456789";
    private const string QueueUrl = "https://aws.fixture.invalid/123456789012/runner-events";

    [Fact]
    public async Task IamGetInstanceProfileReturnsBoundRoles()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml(
            $$"""
            <GetInstanceProfileResponse xmlns="https://iam.amazonaws.com/doc/2010-05-08/">
              <GetInstanceProfileResult>
                <InstanceProfile>
                  <Path>/</Path>
                  <InstanceProfileName>runner-profile</InstanceProfileName>
                  <InstanceProfileId>AIPAEXAMPLE0123456789</InstanceProfileId>
                  <Arn>arn:aws:iam::123456789012:instance-profile/runner-profile</Arn>
                  <CreateDate>2026-01-01T00:00:00Z</CreateDate>
                  <Roles><member>
                    <Path>/</Path><RoleName>runner-bootstrap</RoleName>
                    <RoleId>{{RoleId}}</RoleId><Arn>{{RoleArn}}</Arn>
                    <CreateDate>2026-01-01T00:00:00Z</CreateDate>
                  </member></Roles>
                </InstanceProfile>
              </GetInstanceProfileResult>
              <ResponseMetadata><RequestId>iam-profile</RequestId></ResponseMetadata>
            </GetInstanceProfileResponse>
            """));
        using var client = new AmazonIdentityManagementServiceClient(
            AwsWireTransport.Credentials, wire.Configure(new AmazonIdentityManagementServiceConfig()));

        var response = await client.GetInstanceProfileAsync(new GetInstanceProfileRequest
        {
            InstanceProfileName = "runner-profile",
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(response.InstanceProfile);
        Assert.Equal("arn:aws:iam::123456789012:instance-profile/runner-profile", response.InstanceProfile.Arn);
        Assert.NotNull(response.InstanceProfile.Roles);
        var role = Assert.Single(response.InstanceProfile.Roles);
        Assert.Equal(RoleArn, role.Arn);
        Assert.Equal(RoleId, role.RoleId);
        var form = ReadQuery(Assert.Single(wire.Requests), "GetInstanceProfile", "2010-05-08");
        Assert.Equal("runner-profile", form["InstanceProfileName"]);
    }

    [Fact]
    public async Task IamGetRoleReturnsStableRoleIdForTheExactName()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml(
            $$"""
            <GetRoleResponse xmlns="https://iam.amazonaws.com/doc/2010-05-08/">
              <GetRoleResult>
                <Role>
                  <Path>/</Path><RoleName>runner-bootstrap</RoleName>
                  <RoleId>{{RoleId}}</RoleId><Arn>{{RoleArn}}</Arn>
                  <CreateDate>2026-01-01T00:00:00Z</CreateDate>
                </Role>
              </GetRoleResult>
              <ResponseMetadata><RequestId>iam-role</RequestId></ResponseMetadata>
            </GetRoleResponse>
            """));
        using var client = new AmazonIdentityManagementServiceClient(
            AwsWireTransport.Credentials, wire.Configure(new AmazonIdentityManagementServiceConfig()));

        var response = await client.GetRoleAsync(new GetRoleRequest
        {
            RoleName = "runner-bootstrap",
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(response.Role);
        Assert.Equal(RoleArn, response.Role.Arn);
        Assert.Equal(RoleId, response.Role.RoleId);
        var form = ReadQuery(Assert.Single(wire.Requests), "GetRole", "2010-05-08");
        Assert.Equal("runner-bootstrap", form["RoleName"]);
    }

    [Fact]
    public async Task GetSecretValueSendsCurrentStageAndPreservesVersionIdentity()
    {
        const string secretArn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:runner-app-ABCDEF";
        const string version = "0123456789abcdef0123456789abcdef";
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json(
            $$"""
            {
              "ARN": "{{secretArn}}",
              "Name": "runner-app",
              "VersionId": "{{version}}",
              "VersionStages": ["AWSCURRENT"],
              "SecretString": "{\"token\":\"fixture-only-token\"}"
            }
            """));
        using var client = new AmazonSecretsManagerClient(
            AwsWireTransport.Credentials, wire.Configure(new AmazonSecretsManagerConfig()));

        var response = await client.GetSecretValueAsync(new GetSecretValueRequest
        {
            SecretId = secretArn,
            VersionStage = "AWSCURRENT",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(secretArn, response.ARN);
        Assert.Equal(version, response.VersionId);
        Assert.NotNull(response.VersionStages);
        Assert.Equal("AWSCURRENT", Assert.Single(response.VersionStages));
        Assert.Equal("""{"token":"fixture-only-token"}""", response.SecretString);
        using var json = ReadJson(Assert.Single(wire.Requests), "secretsmanager.GetSecretValue");
        Assert.Equal(secretArn, json.RootElement.GetProperty("SecretId").GetString());
        Assert.Equal("AWSCURRENT", json.RootElement.GetProperty("VersionStage").GetString());
        Assert.False(json.RootElement.TryGetProperty("VersionId", out _));
    }

    [Fact]
    public async Task S3GetObjectPinsVersionAndReturnsBytesAndVersion()
    {
        const string bucket = "runner-config";
        const string key = "profiles/example+blue/runtime config.json";
        const string version = "recorded+object/version=";
        const string document = """{"deployment":"example"}""";
        using var wire = new AwsWireTransport(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(document, Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("x-amz-version-id", version);
            return response;
        });
        var config = wire.Configure(new AmazonS3Config());
        config.ForcePathStyle = true;
        using var client = new AmazonS3Client(AwsWireTransport.Credentials, config);

        using var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = version,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(version, response.VersionId);
        using var reader = new StreamReader(response.ResponseStream);
        Assert.Equal(document, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));

        var send = Assert.Single(wire.Requests);
        Assert.Equal(HttpMethod.Get, send.Method);
        Assert.Equal($"/{bucket}/profiles/example%2Bblue/runtime%20config.json", send.Uri.AbsolutePath);
        Assert.Equal(version, HttpUtility.ParseQueryString(send.Uri.Query)["versionId"]);
        Assert.Empty(send.Body);
    }

    [Fact]
    public async Task SqsReceiveAndDeleteUseExactQueueAndReceiptHandle()
    {
        using var wire = new AwsWireTransport(send => send.Target switch
        {
            "AmazonSQS.ReceiveMessage" => AwsWireTransport.Json(
                """
                {
                  "Messages": [
                    {
                      "Body": "fixture event",
                      "MD5OfBody": "6449ab2989d48f1588a6238cb02a73be",
                      "MessageId": "message-123",
                      "ReceiptHandle": "receipt+/=fixture"
                    }
                  ]
                }
                """),
            "AmazonSQS.DeleteMessage" => AwsWireTransport.Json("{}"),
            _ => throw new InvalidOperationException($"Unexpected SQS request target {send.Target}."),
        });
        using var client = new AmazonSQSClient(AwsWireTransport.Credentials, wire.Configure(new AmazonSQSConfig()));

        var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = QueueUrl,
            WaitTimeSeconds = 5,
            MaxNumberOfMessages = 3,
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(response.Messages);
        var message = Assert.Single(response.Messages);
        Assert.Equal("fixture event", message.Body);
        Assert.Equal("message-123", message.MessageId);
        Assert.Equal("receipt+/=fixture", message.ReceiptHandle);

        await client.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = QueueUrl,
            ReceiptHandle = message.ReceiptHandle,
        }, TestContext.Current.CancellationToken);

        var sends = wire.Requests;
        Assert.Equal(2, sends.Count);
        using var receive = ReadJson(sends[0], "AmazonSQS.ReceiveMessage");
        Assert.Equal(QueueUrl, receive.RootElement.GetProperty("QueueUrl").GetString());
        Assert.Equal(5, receive.RootElement.GetProperty("WaitTimeSeconds").GetInt32());
        Assert.Equal(3, receive.RootElement.GetProperty("MaxNumberOfMessages").GetInt32());
        using var delete = ReadJson(sends[1], "AmazonSQS.DeleteMessage");
        Assert.Equal(QueueUrl, delete.RootElement.GetProperty("QueueUrl").GetString());
        Assert.Equal("receipt+/=fixture", delete.RootElement.GetProperty("ReceiptHandle").GetString());
    }

    [Fact]
    public async Task StsGetCallerIdentityPreservesTheRoleSessionUserId()
    {
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Xml(
            $$"""
            <GetCallerIdentityResponse xmlns="https://sts.amazonaws.com/doc/2011-06-15/">
              <GetCallerIdentityResult>
                <Arn>arn:aws:sts::123456789012:assumed-role/runner-bootstrap/i-0123456789abcdef0</Arn>
                <UserId>{{RoleId}}:i-0123456789abcdef0</UserId>
                <Account>123456789012</Account>
              </GetCallerIdentityResult>
              <ResponseMetadata><RequestId>sts-identity</RequestId></ResponseMetadata>
            </GetCallerIdentityResponse>
            """));
        using var client = new AmazonSecurityTokenServiceClient(
            AwsWireTransport.Credentials, wire.Configure(new AmazonSecurityTokenServiceConfig()));

        var response = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(),
            TestContext.Current.CancellationToken);

        Assert.Equal($"{RoleId}:i-0123456789abcdef0", response.UserId);
        Assert.Equal("123456789012", response.Account);
        Assert.Equal("arn:aws:sts::123456789012:assumed-role/runner-bootstrap/i-0123456789abcdef0", response.Arn);
        ReadQuery(Assert.Single(wire.Requests), "GetCallerIdentity", "2011-06-15");
    }

    private static NameValueCollection ReadQuery(AwsWireRequest send, string operation, string version)
    {
        Assert.Equal(HttpMethod.Post, send.Method);
        Assert.Equal("/", send.Uri.AbsolutePath);
        Assert.Null(send.Target);
        var form = HttpUtility.ParseQueryString(send.Body);
        Assert.Equal(operation, form["Action"]);
        Assert.Equal(version, form["Version"]);
        return form;
    }

    private static JsonDocument ReadJson(AwsWireRequest send, string target)
    {
        Assert.Equal(HttpMethod.Post, send.Method);
        Assert.Equal(target, send.Target);
        return JsonDocument.Parse(send.Body);
    }
}
