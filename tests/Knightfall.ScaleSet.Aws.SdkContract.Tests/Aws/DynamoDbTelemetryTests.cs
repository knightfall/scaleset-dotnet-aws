using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Amazon.DynamoDBv2;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Telemetry;
using Knightfall.ScaleSet.Aws.DynamoDb;
using Xunit;

namespace Knightfall.ScaleSet.Aws.SdkContract.Tests.Aws;

public sealed class DynamoDbTelemetryTests
{
    private const string SyntheticPayload = "fixture-private-item-and-error-value";
    private const string OwnerTask = "arn:aws:ecs:us-east-1:111122223333:task/controller/00000000000000000000000000000001";

    [Theory]
    [InlineData(true, LifecycleWriteOutcome.LeaseRejected, "fenced", 0)]
    [InlineData(false, LifecycleWriteOutcome.Conflict, "conflict", 1)]
    public async Task TransactionTelemetryReportsTypedFenceAndConflictOutcomesWithoutItemData(
        bool fenced, LifecycleWriteOutcome expected, string outcome, int conflictCount)
    {
        var token = TestContext.Current.CancellationToken;
        var runtime = RuntimeConfigurationLoader.Load(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "fargate-only.json"), token));
        var configured = Assert.Single(runtime.Profiles);
        var telemetryProfile = Assert.IsType<TelemetryProfile>(TelemetryProfile.From(configured with { Id = "ddb-telemetry-fixture-" + Guid.NewGuid().ToString("N") }));
        using var scope = TelemetryProfileScope.Enter(telemetryProfile);
        using var parent = new Activity("ddb-telemetry-fixture").SetIdFormat(ActivityIdFormat.W3C).Start();
        var spans = new ConcurrentQueue<Activity>();
        using var tracer = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Knightfall.ScaleSet.Aws.DynamoDb",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                if (span.TraceId == parent.TraceId)
                {
                    spans.Enqueue(span);
                }
            },
        };
        ActivitySource.AddActivityListener(tracer);
        var conflicts = new ConcurrentQueue<long>();
        using var meter = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "scaleset.aws.dynamodb.conflicts")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meter.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var fields = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (fields.GetValueOrDefault("profile") is string id && id == telemetryProfile.Id)
            {
                Assert.Equal("transaction", fields["operation"]);
                Assert.Equal("conflict", fields["outcome"]);
                conflicts.Enqueue(value);
            }
        });
        meter.Start();
        string first = fenced ? "ConditionalCheckFailed" : "None";
        string second = fenced ? "None" : "TransactionConflict";
        using var wire = new AwsWireTransport(_ => AwsWireTransport.Json($$"""
            {
              "__type": "TransactionCanceledException",
              "message": "{{SyntheticPayload}}",
              "CancellationReasons": [{"Code":"{{first}}"}, {"Code":"{{second}}"}]
            }
            """, HttpStatusCode.BadRequest));
        using var client = new AmazonDynamoDBClient(AwsWireTransport.Credentials, wire.Configure(new AmazonDynamoDBConfig()));
        var store = new DynamoDbLifecycleStore(client, "ddb-telemetry-test", TimeProvider.System);
        var fence = new DeploymentLeaseFence("fixture-deployment", "fixture-owner", 1, ControllerLease.CurrentWriterProtocol, OwnerTask);

        var result = await store.WriteRecordsAsync(fence, Guid.NewGuid(),
            [new LifecycleRecordWrite(LifecycleKey.Claim("fixture-claim"), null, SyntheticPayload)], token);

        Assert.Equal(expected, result);
        Assert.Single(wire.Requests);
        var span = Assert.Single(spans);
        Assert.Equal("scaleset.aws.dynamodb.transaction", span.OperationName);
        Assert.Equal(outcome, span.GetTagItem("outcome"));
        Assert.Equal("fargate", span.GetTagItem("provider"));
        Assert.Equal(telemetryProfile.Id, span.GetTagItem("profile"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value?.ToString()?.Contains(SyntheticPayload, StringComparison.Ordinal) == true);
        Assert.Equal(conflictCount, conflicts.Sum());
    }
}
