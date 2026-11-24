using System.Collections.Frozen;
using System.Net;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Knightfall.ScaleSet.Aws.Controller.Configuration;
using Knightfall.ScaleSet.Aws.Core.Configuration;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Xunit;

namespace Knightfall.ScaleSet.Aws.Core.Tests;

public sealed class FargateQuotaReaderTests
{
    private static readonly AwsEnvironment Aws = new("111122223333", "ap-southeast-2");
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly FargateQuotaObject Source = new("approved-quota-samples", "usage/on-demand.json");

    [Fact]
    public async Task SharesOneSampleAndHoldsExpiredOrUnavailableEvidenceUntilNewObservation()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock(Now);
        using var s3 = new QuotaS3Client();
        var first = Sample(Now, available: 1024);
        s3.Reply(first);
        var reader = new FargateQuotaReader(Aws, Evidence(first, Source), s3, clock);

        Assert.Equal(first, await reader.ReadAsync(token));
        Assert.Equal(first, await reader.ReadAsync(token));
        var request = Assert.Single(s3.Requests);
        Assert.Equal(Source.Bucket, request.BucketName);
        Assert.Equal(Source.Key, request.Key);
        Assert.Equal(Aws.Account, request.ExpectedBucketOwner);
        Assert.Null(request.VersionId);

        clock.Now += TimeSpan.FromMinutes(10).Add(TimeSpan.FromTicks(1));
        Assert.Null(await reader.ReadAsync(token));
        clock.Now += TimeSpan.FromSeconds(30);
        s3.Error = new AmazonS3Exception("synthetic outage") { StatusCode = HttpStatusCode.ServiceUnavailable };
        Assert.Null(await reader.ReadAsync(token));
        Assert.Null(await reader.ReadAsync(token));
        Assert.Equal(3, s3.Requests.Count);

        clock.Now += TimeSpan.FromSeconds(30);
        var freshZero = Sample(clock.Now, available: 0);
        s3.Reply(freshZero);
        Assert.Equal(freshZero, await reader.ReadAsync(token));
        clock.Now += TimeSpan.FromSeconds(30);
        s3.Reply(freshZero with { ObservedAt = freshZero.ObservedAt.AddSeconds(-1), RemainingCpuUnits = 4096 });
        Assert.Null(await reader.ReadAsync(token));
        clock.Now += TimeSpan.FromSeconds(30);
        var fresh = Sample(clock.Now, available: 2048);
        s3.Reply(fresh);
        Assert.Equal(fresh, await reader.ReadAsync(token));
    }

    [Fact]
    public async Task ConfiguredObjectDoesNotFallBackToInlineEvidenceOnInvalidSample()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock(Now);
        using var s3 = new QuotaS3Client();
        var inline = Sample(Now, available: 4096);
        var reader = new FargateQuotaReader(Aws, Evidence(inline, Source), s3, clock);

        s3.Reply(inline with { Account = "111122223334" });
        Assert.Null(await reader.ReadAsync(token));
        clock.Now += TimeSpan.FromSeconds(30);
        s3.Reply(inline with { ExpiresAt = clock.Now });
        Assert.Null(await reader.ReadAsync(token));
        clock.Now += TimeSpan.FromSeconds(30);
        s3.Bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            account = Aws.Account,
            region = Aws.Region,
            observedAt = clock.Now,
            expiresAt = clock.Now.AddMinutes(1),
            remainingCpuUnits = 4096,
        });
        Assert.Null(await reader.ReadAsync(token)); // Reserved margin is required.
        clock.Now += TimeSpan.FromSeconds(30);
        s3.Bytes = new byte[4097];
        Assert.Null(await reader.ReadAsync(token));
    }

    [Fact]
    public async Task LegacyInlineEvidencePreservesZeroAndExplicitExpiry()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new ManualClock(Now);
        var inline = Sample(Now, available: 0) with { ExpiresAt = Now.AddMinutes(1) };
        var reader = new FargateQuotaReader(Aws, Evidence(inline, source: null), s3: null, clock);
        Assert.Equal(inline, await reader.ReadAsync(token));
        clock.Now = inline.ExpiresAt;
        Assert.Null(await reader.ReadAsync(token));
    }

    private static FargateRuntimeEvidence Evidence(FargateQuotaEvidence inline, FargateQuotaObject? source) =>
        new(FrozenDictionary<string, FargateContainerApproval>.Empty, inline, source);

    private static FargateQuotaEvidence Sample(DateTimeOffset observedAt, long available) =>
        new(Aws.Account, Aws.Region, observedAt, observedAt.AddMinutes(15), available, 0);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class QuotaS3Client() : AmazonS3Client(new BasicAWSCredentials("fixture", "fixture"), RegionEndpoint.APSoutheast2)
    {
        internal List<GetObjectRequest> Requests { get; } = [];
        internal AmazonS3Exception? Error { get; set; }
        internal byte[] Bytes { get; set; } = [];

        internal void Reply(FargateQuotaEvidence sample)
        {
            Error = null;
            Bytes = JsonSerializer.SerializeToUtf8Bytes(sample, JsonSerializerOptions.Web);
        }

        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Error is { } error)
            {
                return Task.FromException<GetObjectResponse>(error);
            }
            return Task.FromResult(new GetObjectResponse
            {
                ContentLength = Bytes.Length,
                ResponseStream = new MemoryStream(Bytes),
            });
        }
    }
}
