using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.SecurityToken.Model;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

internal sealed partial class MiniStackIamFixture : IDisposable
{
    internal const string Account = "111122223333";
    internal const string Region = "ap-southeast-2";
    internal const string EndpointVariable = "SCALESET_MINISTACK_IAM_ENDPOINT";

    private readonly Dictionary<string, MiniStackIamRole> _roles = new(StringComparer.Ordinal);

    private MiniStackIamFixture(Uri endpoint)
    {
        Endpoint = endpoint;
        Controller = new MiniStackIamClients(endpoint, new BasicAWSCredentials(Account, "test-only-secret"));
    }

    internal Uri Endpoint { get; }

    internal string TableName { get; } = "authorized-integration-" + Guid.NewGuid().ToString("N");

    internal string TableArn => $"arn:aws:dynamodb:{Region}:{Account}:table/{TableName}";

    internal MiniStackIamClients Controller { get; }

    internal static async Task<MiniStackIamFixture> CreateAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable(EndpointVariable), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Host != "127.0.0.1" || endpoint.IsDefaultPort ||
            endpoint.UserInfo.Length != 0 || endpoint.PathAndQuery != "/" || endpoint.Fragment.Length != 0)
        {
            throw new InvalidOperationException("Use make check-integration to provide the authenticated loopback MiniStack endpoint.");
        }

        var fixture = new MiniStackIamFixture(endpoint);
        try
        {
            var caller = await fixture.Controller.Sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken)
                .ConfigureAwait(false);
            if (caller.Account != Account || caller.UserId != Account || caller.Arn != $"arn:aws:iam::{Account}:root")
            {
                throw new InvalidOperationException("The MiniStack setup caller does not match the fixture account.");
            }

            var created = await fixture.Controller.DynamoDb.CreateTableAsync(new CreateTableRequest
            {
                TableName = fixture.TableName,
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
            }, cancellationToken).ConfigureAwait(false);
            if (created.TableDescription?.TableArn != fixture.TableArn)
            {
                throw new InvalidOperationException("The authenticated fixture table does not match the account and Region.");
            }
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    internal async Task<MiniStackIamRole> EnsureRunnerRoleAsync(string roleName, bool includeStatus, CancellationToken cancellationToken)
    {
        if (!RoleName().IsMatch(roleName))
        {
            throw new ArgumentException("A canonical fixture role name is required.", nameof(roleName));
        }
        if (_roles.TryGetValue(roleName, out var previous))
        {
            return previous.IncludeStatus == includeStatus ? previous : throw new InvalidOperationException("The fixture role purpose differs.");
        }

        // The local setup principal stands in for the instance/task platform when obtaining test sessions.
        var trust = new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Effect = "Allow",
                    Action = "sts:AssumeRole",
                    Principal = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["AWS"] = $"arn:aws:iam::{Account}:root",
                        ["Service"] = includeStatus ? "ec2.amazonaws.com" : "ecs-tasks.amazonaws.com",
                    },
                },
            },
        };
        var result = await Controller.Iam.CreateRoleAsync(new CreateRoleRequest
        {
            RoleName = roleName,
            AssumeRolePolicyDocument = JsonSerializer.Serialize(trust),
        }, cancellationToken).ConfigureAwait(false);
        var created = result.Role;
        if (created?.RoleName != roleName || created.Arn != $"arn:aws:iam::{Account}:role/{roleName}" ||
            created.RoleId is not { } roleId || !RoleIdentifier().IsMatch(roleId))
        {
            throw new InvalidOperationException("MiniStack did not return the exact fixture role.");
        }

        var role = new MiniStackIamRole(roleName, created.Arn, roleId, includeStatus);
        await Controller.Iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = role.Name,
            PolicyName = "BootstrapTable",
            PolicyDocument = BootstrapPolicy(role.RoleId + ":*", "ForAllValues:StringLike", includeStatus),
        }, cancellationToken).ConfigureAwait(false);
        _roles.Add(role.Name, role);
        return role;
    }

    internal async Task<MiniStackIamSession> AssumeRunnerAsync(MiniStackIamRole role, string shortInstanceOrTaskId,
        CancellationToken cancellationToken)
    {
        if (!_roles.TryGetValue(role.Name, out var created) || created != role)
        {
            throw new ArgumentException("The role must originate from this fixture.", nameof(role));
        }
        var sessionId = role.IncludeStatus ? InstanceId() : TaskId();
        if (!sessionId.IsMatch(shortInstanceOrTaskId))
        {
            throw new ArgumentException("A canonical short instance or task ID is required.", nameof(shortInstanceOrTaskId));
        }

        var expectedUserId = $"{role.RoleId}:{shortInstanceOrTaskId}";
        var expectedArn = $"arn:aws:sts::{Account}:assumed-role/{role.Name}/{shortInstanceOrTaskId}";
        var issued = await Controller.Sts.AssumeRoleAsync(new AssumeRoleRequest
        {
            RoleArn = role.Arn,
            RoleSessionName = shortInstanceOrTaskId,
            DurationSeconds = 900,
            // MiniStack 1.5.44 does not expand IAM policy variables. Its native STS
            // session policy enforces the exact key after intersecting the role policy.
            Policy = BootstrapPolicy(expectedUserId, "ForAllValues:StringEquals", role.IncludeStatus),
        }, cancellationToken).ConfigureAwait(false);
        if (issued.AssumedRoleUser is not { } user || user.AssumedRoleId != expectedUserId || user.Arn != expectedArn ||
            issued.Credentials is not { AccessKeyId.Length: > 0, SecretAccessKey.Length: > 0, SessionToken.Length: > 0 } credential)
        {
            throw new InvalidOperationException("MiniStack did not issue the exact fixture session.");
        }

        var credentials = new SessionAWSCredentials(credential.AccessKeyId, credential.SecretAccessKey, credential.SessionToken);
        var clients = new MiniStackIamClients(Endpoint, credentials);
        try
        {
            var caller = await clients.Sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken).ConfigureAwait(false);
            if (caller.Account != Account || caller.UserId != expectedUserId || caller.Arn != expectedArn)
            {
                throw new InvalidOperationException("Signed MiniStack caller identity differs from the issued session.");
            }
            return new MiniStackIamSession(clients, credentials, caller.Arn, caller.UserId);
        }
        catch
        {
            clients.Dispose();
            throw;
        }
    }

    public void Dispose() => Controller.Dispose();

    private string BootstrapPolicy(string identity, string comparison, bool includeStatus)
    {
        var permissions = new List<(string Action, string Partition)>
        {
            ("dynamodb:PutItem", "HELLO#"),
            ("dynamodb:DeleteItem", "OFFER#"),
        };
        if (includeStatus)
        {
            permissions.Add(("dynamodb:PutItem", "STATUS#"));
        }
        return JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = permissions.Select(permission => new
            {
                Effect = "Allow",
                permission.Action,
                Resource = TableArn,
                Condition = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    [comparison] = new Dictionary<string, string[]> { ["dynamodb:LeadingKeys"] = [permission.Partition + identity] },
                    ["Null"] = new Dictionary<string, string> { ["dynamodb:LeadingKeys"] = "false" },
                },
            }),
        });
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_+=,.@-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleName();

    [GeneratedRegex(@"\AAROA[A-Z0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleIdentifier();

    [GeneratedRegex(@"\Ai-(?:[a-f0-9]{8}|[a-f0-9]{17})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceId();

    [GeneratedRegex(@"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TaskId();
}

internal sealed record MiniStackIamRole(string Name, string Arn, string RoleId, bool IncludeStatus);

internal sealed class MiniStackIamSession(MiniStackIamClients clients, SessionAWSCredentials credentials, string arn, string awsUserId) : IDisposable
{
    internal MiniStackIamClients Clients { get; } = clients;

    internal string Arn { get; } = arn;

    internal string AwsUserId { get; } = awsUserId;

    internal SessionAWSCredentials Credentials { get; } = credentials;

    public void Dispose() => Clients.Dispose();
}
