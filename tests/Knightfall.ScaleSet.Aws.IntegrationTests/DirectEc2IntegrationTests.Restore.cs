using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.EC2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Knightfall.ScaleSet.Aws.Core.Catalog;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.Lifecycle.Tests;
using Xunit;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

public sealed partial class DirectEc2IntegrationTests
{
    [Fact]
    public async Task NativeBackupRestorePurgesBootstrapRowsAndSettlesThePostBackupLaunchWithoutResending()
    {
        var token = TestContext.Current.CancellationToken;
        using var cloud = await MiniStackIamFixture.CreateAsync(token);
        var role = await cloud.EnsureRunnerRoleAsync(DirectEc2Fixture.RoleName(), includeStatus: true, token);
        using var bootstrap = await cloud.AssumeRunnerAsync(role, "i-50910000000000001", token);
        using var ec2 = await DirectEc2Fixture.CreateAsync(cloud.Endpoint, cloud.Controller.Credentials,
            cloud.Controller.Ec2, cloud.Controller.Iam, Account, Region, role.Arn, token);
        var predecessor = await RestoreOwnerTask.StartAsync(cloud.Controller.Ecs, ec2.SecondSubnet, token);
        Assert.Equal(ControllerTaskFenceStatus.Running, await predecessor.ReadAsync(predecessor.ControllerArn, token));

        var clock = new FixtureClock();
        var configuration = await ec2.ConfigurationAsync(cloud.TableName, "1", token);
        var sourceClient = cloud.Controller.DynamoDb;
        var source = new DynamoDbLifecycleStore(sourceClient, cloud.TableName, clock);
        var acquired = await AcquireAsync(source, configuration, predecessor.ControllerArn, clock, token);
        var sourceAuthority = new ControllerLeaseRecoveryCoordinator(source, predecessor, clock, WriteReserve);
        var activated = await sourceAuthority.TryActivateAsync(acquired, configuration, ClockProof(clock), [], token);
        Assert.Equal(LeaseActivationStatus.Activated, activated.Status);
        var owner = Assert.IsType<ControllerLease>(activated.Lease);
        await ApplyRestoreCatalogAsync(source, owner, configuration, 1, token);
        var key = new LifecycleProfile(owner.Deployment, Assert.Single(configuration.Profiles).Id);
        var catalog = await BindCatalogAsync(source, key, owner, token);
        var discarded = RestoreBootstrapRows(bootstrap.AwsUserId);
        await sourceClient.PutItemAsync(cloud.TableName, discarded[0], token);
        await sourceClient.PutItemAsync(cloud.TableName, discarded[1], token);
        await bootstrap.Clients.DynamoDb.PutItemAsync(cloud.TableName, discarded[2], token);
        await sourceClient.PutItemAsync(cloud.TableName, discarded[3], token);
        await sourceClient.PutItemAsync(cloud.TableName, discarded[4], token);

        using var nativeProvider = ec2.Provider(configuration, key, clock);
        var launch = Assert.IsType<Ec2LaunchEffectRequest>(await nativeProvider.CreateLaunchRequestAsync(Guid.NewGuid(),
            attemptOrdinal: 0, placementSequence: 1, new HashSet<string>(StringComparer.Ordinal), null, token));
        Assert.Equal(ec2.SecondSubnet, launch.SubnetId);
        var intent = EffectIntentIdentity.ForEc2Launch(launch.AllocationId, launch.AttemptOrdinal);
        var observed = clock.GetUtcNow();
        var envelope = EffectEnvelope.Create(key, intent, catalog.AppliedGeneration, launch, observed.AddSeconds(-1), observed.AddMinutes(1));
        var sourceJournal = new DynamoDbEffectJournal(source, clock);
        var sourceDispatcher = new DynamoDbEffectDispatcher(source, clock, Timing);
        Assert.Equal(LifecycleWriteOutcome.Committed, await sourceJournal.PlanAsync(owner, envelope, Guid.NewGuid(), token));

        var capture = new BackupBeforeSend(nativeProvider, sourceJournal, sourceClient, cloud.TableName, ec2.Wire);
        Assert.Equal(EffectDispatchStatus.Settled, await sourceDispatcher.DispatchPlannedAsync(owner, ClockProof(clock), key, intent,
            capture, CancellationToken.None, token));
        var backup = Assert.IsType<BackupDetails>(capture.Backup);
        Assert.Equal(BackupStatus.AVAILABLE, backup.BackupStatus);
        var description = await sourceClient.DescribeBackupAsync(new DescribeBackupRequest { BackupArn = backup.BackupArn }, token);
        Assert.Equal(cloud.TableArn, description.BackupDescription.SourceTableDetails.TableArn);
        var sourceSettled = Assert.IsType<EffectJournalSnapshot>(await sourceJournal.ReadAsync(key, intent, token));
        Assert.Equal(EffectState.Settled, sourceSettled.Entry.State);
        var allocation = launch.AllocationId.ToString("D");
        var sourceRunner = Assert.IsType<ExecutorSnapshot>(await source.ReadExecutorAsync(key, allocation, token));
        Assert.Equal(ExecutorResourceState.Running, sourceRunner.State.Resource?.State);

        var marker = RestoreKey("fixture-post-backup");
        await sourceClient.PutItemAsync(cloud.TableName, marker, token);
        await AssertExportMetadataAsync(cloud, token);
        await predecessor.StopAsync(token);
        Assert.Equal(ControllerTaskFenceStatus.Stopped, await predecessor.ReadAsync(owner.OwnerTaskArn, token));

        string targetName = cloud.TableName + "-restored";
        var restored = await sourceClient.RestoreTableFromBackupAsync(new RestoreTableFromBackupRequest
        {
            BackupArn = backup.BackupArn,
            TargetTableName = targetName,
        }, token);
        Assert.Equal(backup.BackupArn, restored.TableDescription.RestoreSummary.SourceBackupArn);
        Assert.Equal($"{cloud.TableArn}-restored", restored.TableDescription.TableArn);
        Assert.Equal("due", Assert.Single(restored.TableDescription.GlobalSecondaryIndexes).IndexName);

        using var targetClient = RestoredTableDynamoWire.CreateClient(cloud.Controller, targetName);
        var target = new DynamoDbLifecycleStore(targetClient, targetName, clock);
        var targetJournal = new DynamoDbEffectJournal(target, clock);
        var targetBefore = Assert.IsType<EffectJournalSnapshot>(await targetJournal.ReadAsync(key, intent, token));
        Assert.Equal(EffectState.Executing, targetBefore.Entry.State);
        Assert.Equal(capture.Snapshot?.TransitionId, targetBefore.TransitionId);
        Assert.Equal(capture.Snapshot?.Entry.Attempt, targetBefore.Entry.Attempt);
        Assert.Equal(envelope.Sha256, targetBefore.Entry.Envelope.Sha256);
        Assert.Null(await target.ReadExecutorAsync(key, allocation, token));
        Assert.Empty((await targetClient.GetItemAsync(targetName, marker, consistentRead: true, token)).Item ?? []);

        await AssertBootstrapCannotAccessTargetAsync(bootstrap, targetName, token);
        var beforePurge = await ReadBootstrapKeysAsync(targetClient, targetName, token);
        Assert.Equal(discarded.Length, beforePurge.Count);
        foreach (var row in beforePurge)
        {
            await targetClient.DeleteItemAsync(targetName, row, token);
        }
        Assert.Empty(await ReadBootstrapKeysAsync(targetClient, targetName, token));
        var purgedJournal = Assert.IsType<EffectJournalSnapshot>(await targetJournal.ReadAsync(key, intent, token));
        Assert.Equal(targetBefore.TransitionId, purgedJournal.TransitionId);
        Assert.Equal(targetBefore.Entry.Attempt, purgedJournal.Entry.Attempt);

        var restoredConfiguration = await ec2.ConfigurationAsync(targetName, "1", token);
        var targetAuthority = new ControllerLeaseRecoveryCoordinator(target, predecessor, clock, WriteReserve);
        clock.Set(owner.ValidUntil.AddSeconds(2));
        var successor = await AcquireAsync(target, restoredConfiguration, SecondTask, clock, token);
        Assert.Equal(owner.OwnerTaskArn, successor.RequiredFencingTaskArn);
        Assert.Equal(LeaseActivationStatus.ProfileRecoveryPending,
            (await targetAuthority.TryActivateAsync(successor, restoredConfiguration, ClockProof(clock), [], token)).Status);
        var targetDispatcher = new DynamoDbEffectDispatcher(target, clock, Timing);
        using var recoveryProvider = ec2.Provider(restoredConfiguration, key, clock);
        Assert.Equal(EffectDispatchStatus.OwnershipUnavailable, await targetDispatcher.DispatchPlannedAsync(owner, ClockProof(clock),
            key, intent, recoveryProvider, CancellationToken.None, token));
        var recovery = new EffectRecoveryAuthority(targetAuthority, []);
        var pass = await targetDispatcher.ReconcilePassAsync(successor, ClockProof(clock), key, null, recoveryProvider, recovery, token);
        Assert.Equal((LifecycleWriteOutcome.Committed, 1), (pass.Outcome, pass.Settled));
        var recovered = Assert.IsType<ExecutorSnapshot>(await target.ReadExecutorAsync(key, allocation, token));
        Assert.Equal(sourceRunner.Binding, recovered.Binding);
        Assert.Equal(ExecutorResourceState.Running, recovered.State.Resource?.State);
        var settlement = Assert.IsType<EffectJournalSnapshot>(await targetJournal.ReadAsync(key, intent, token));
        Assert.Equal(EffectResolutionKind.Applied, settlement.Entry.Resolution?.Kind);
        Assert.Equal(recovered.TransitionId, settlement.TransitionId);
        var promoted = await targetAuthority.TryActivateAsync(successor, restoredConfiguration, ClockProof(clock), [], token);
        Assert.Equal(LeaseActivationStatus.Activated, promoted.Status);
        var active = Assert.IsType<ControllerLease>(promoted.Lease);
        var repeat = await targetDispatcher.ReconcilePassAsync(active, ClockProof(clock), key, null, recoveryProvider, recovery: null, token);
        Assert.Equal((LifecycleWriteOutcome.Committed, 0), (repeat.Outcome, repeat.Settled));
        Assert.Equal(CatalogRolloutStatus.StaleGeneration,
            (await target.AdvanceCatalogRolloutAsync(active.Fence, restoredConfiguration, 1, token)).Status);
        await ApplyRestoreCatalogAsync(target, active, restoredConfiguration, 2, token);

        await AssertBootstrapCannotAccessTargetAsync(bootstrap, targetName, token);
        using var reopened = await cloud.OpenRestoredAsync(targetName, role, token);
        using var freshBootstrap = await reopened.AssumeRunnerAsync(role, InstanceId(recovered.Binding), token);
        var denied = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(() => freshBootstrap.Clients.DynamoDb.PutItemAsync(targetName,
            RowKey(LifecycleKey.Hello(freshBootstrap.AwsUserId)), token));
        Assert.Equal("AccessDeniedException", denied.ErrorCode);
        using var receiver = JitRecipientKey.Create();
        Assert.Equal(LifecycleWriteOutcome.Committed, await BrokerHelloFixture.RegisterAsync(targetClient, targetName,
            freshBootstrap.AwsUserId, receiver.Hello, clock, token));
        Assert.Single(await ReadBootstrapKeysAsync(targetClient, targetName, token));
        await AssertBootstrapCannotAccessTargetAsync(bootstrap, targetName, token);

        Assert.Equal(owner, await source.ReadLeaseAsync(owner.Deployment, token));
        Assert.Equal(sourceSettled.TransitionId, (await sourceJournal.ReadAsync(key, intent, token))?.TransitionId);
        Assert.NotEmpty((await sourceClient.GetItemAsync(cloud.TableName, marker, consistentRead: true, token)).Item);
        Assert.Equal(discarded.Length, (await ReadBootstrapKeysAsync(sourceClient, cloud.TableName, token)).Count);
        var sent = Assert.Single(ec2.Wire.Calls("RunInstances"));
        Assert.Equal((ec2.SecondSubnet, launch.ClientToken.ToString("D"), "1"),
            (sent["SubnetId"], sent["ClientToken"], sent["LaunchTemplate.Version"]));
        Assert.Empty(ec2.Wire.Calls("TerminateInstances"));
        var instance = Assert.Single((await cloud.Controller.Ec2.DescribeInstancesAsync(new DescribeInstancesRequest
        {
            InstanceIds = [InstanceId(recovered.Binding)],
        }, token)).Reservations.SelectMany(reservation => reservation.Instances));
        Assert.Equal("running", instance.State.Name.Value);
    }

    private static async Task ApplyRestoreCatalogAsync(DynamoDbLifecycleStore store, ControllerLease lease,
        RuntimeConfiguration configuration, long generation, CancellationToken cancellationToken)
    {
        for (int pass = 0; pass < 8; pass++)
        {
            var change = await store.AdvanceCatalogRolloutAsync(lease.Fence, configuration, generation, cancellationToken);
            if (change.Status == CatalogRolloutStatus.Applied)
            {
                Assert.Equal(configuration.Sha256, change.Head?.AppliedDigest);
                return;
            }
            Assert.Equal(CatalogRolloutStatus.Staging, change.Status);
        }
        throw new InvalidOperationException("The restore drill catalog did not publish.");
    }

    private static Dictionary<string, AttributeValue>[] RestoreBootstrapRows(string awsUserId) =>
    [
        RowKey(LifecycleKey.Hello(awsUserId)),
        RowKey(LifecycleKey.Offer(awsUserId)),
        RowKey(LifecycleKey.Status(awsUserId)),
        RestoreKey("STATUS#untrusted-legacy"),
        RestoreKey("HELLO#" + awsUserId),
    ];

    private static Dictionary<string, AttributeValue> RestoreKey(string partition) => new(StringComparer.Ordinal)
    {
        ["PK"] = new(partition),
        ["SK"] = new("snapshot-fixture"),
    };

    private static async Task<List<Dictionary<string, AttributeValue>>> ReadBootstrapKeysAsync(AmazonDynamoDBClient client, string table,
        CancellationToken cancellationToken)
    {
        var rows = new List<Dictionary<string, AttributeValue>>();
        var request = new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            Limit = 2,
            ProjectionExpression = "PK, SK",
            FilterExpression = "begins_with(PK, :hello) OR begins_with(PK, :offer) OR begins_with(PK, :status)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":hello"] = new("HELLO#"),
                [":offer"] = new("OFFER#"),
                [":status"] = new("STATUS#"),
            },
        };
        do
        {
            var page = await client.ScanAsync(request, cancellationToken);
            rows.AddRange(page.Items ?? []);
            request.ExclusiveStartKey = page.LastEvaluatedKey;
        }
        while (request.ExclusiveStartKey is { Count: > 0 });
        return rows;
    }

    private static async Task AssertBootstrapCannotAccessTargetAsync(MiniStackIamSession bootstrap, string table, CancellationToken token)
    {
        var delete = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(() => bootstrap.Clients.DynamoDb.DeleteItemAsync(table,
            RowKey(LifecycleKey.Offer(bootstrap.AwsUserId)), token));
        Assert.Equal("AccessDeniedException", delete.ErrorCode);
        var write = await Assert.ThrowsAnyAsync<AmazonDynamoDBException>(() => bootstrap.Clients.DynamoDb.PutItemAsync(table,
            RowKey(LifecycleKey.Hello(bootstrap.AwsUserId)), token));
        Assert.Equal("AccessDeniedException", write.ErrorCode);
    }

    private static async Task AssertExportMetadataAsync(MiniStackIamFixture cloud, CancellationToken token)
    {
        await cloud.Controller.DynamoDb.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
        {
            TableName = cloud.TableName,
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification { PointInTimeRecoveryEnabled = true },
        }, token);
        var options = cloud.Controller.Configure<AmazonS3Config>();
        options.ForcePathStyle = true;
        using var s3 = new AmazonS3Client(cloud.Controller.Credentials, options);
        string bucket = "scaleset-restore-" + Guid.NewGuid().ToString("N");
        await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket, BucketRegionName = Region }, token);
        var requested = await cloud.Controller.DynamoDb.ExportTableToPointInTimeAsync(new ExportTableToPointInTimeRequest
        {
            TableArn = cloud.TableArn,
            S3Bucket = bucket,
        }, token);
        var describe = new DescribeExportRequest { ExportArn = requested.ExportDescription.ExportArn };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        ExportDescription completed;
        do
        {
            completed = (await cloud.Controller.DynamoDb.DescribeExportAsync(describe, timeout.Token)).ExportDescription;
            if (completed.ExportStatus == ExportStatus.IN_PROGRESS)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
            }
        }
        while (completed.ExportStatus == ExportStatus.IN_PROGRESS);
        Assert.Equal(ExportStatus.COMPLETED, completed.ExportStatus);
        Assert.Equal((cloud.TableArn, bucket), (completed.TableArn, completed.S3Bucket));
        Assert.NotEmpty(completed.ExportManifest);
        // MiniStack 1.5.44 reports a manifest path without writing the export to S3.
        Assert.Empty((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, token)).S3Objects ?? []);
    }

    private sealed class BackupBeforeSend(IReconciliationEffectProvider provider, DynamoDbEffectJournal journal, IAmazonDynamoDB client,
        string source, DirectEc2AwsWire wire) : IReconciliationEffectProvider
    {
        internal BackupDetails? Backup { get; private set; }

        internal EffectJournalSnapshot? Snapshot { get; private set; }

        public Task<EffectDispatchPrecondition?> CheckAsync(EffectEnvelope envelope, CatalogBinding binding, CancellationToken cancellationToken) =>
            provider.CheckAsync(envelope, binding, cancellationToken);

        public async Task<EffectEvidence?> SendAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken)
        {
            Assert.Null(Backup);
            Assert.Empty(wire.Calls("RunInstances"));
            Snapshot = Assert.IsType<EffectJournalSnapshot>(await journal.ReadAsync(envelope.Profile, envelope.IntentId, cancellationToken));
            Assert.Equal(EffectState.Executing, Snapshot.Entry.State);
            Assert.Equal(attempt, Snapshot.Entry.Attempt);
            Backup = (await client.CreateBackupAsync(new CreateBackupRequest
            {
                TableName = source,
                BackupName = source + "-before-launch",
            }, cancellationToken)).BackupDetails;
            return await provider.SendAsync(envelope, attempt, cancellationToken);
        }

        public Task<EffectEvidence?> ObserveAsync(EffectEnvelope envelope, EffectAttempt attempt, CancellationToken cancellationToken) =>
            provider.ObserveAsync(envelope, attempt, cancellationToken);
    }
}
