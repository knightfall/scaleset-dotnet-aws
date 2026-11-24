using System.Text.Json.Nodes;
using Knightfall.ScaleSet.Aws.Core.Lifecycle;
using Knightfall.ScaleSet.Aws.Core.Planning;
using Knightfall.ScaleSet.Aws.Providers.Fargate;
using Xunit;
using static Knightfall.ScaleSet.Aws.Providers.Tests.FargateInspectionFixture;

namespace Knightfall.ScaleSet.Aws.Providers.Tests;

public sealed class FargateInventoryTests
{
    private static readonly Guid OtherAllocation = Guid.Parse("10112233-4455-6677-8899-aabbccddeeff");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyInventoryIsKnownOnlyAfterExactActiveClusterVerification(bool clusterMatches)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            ClusterResponse = clusterMatches ? ClusterReply() : ClusterReply(Cluster.Replace(Account, "999900001111", StringComparison.Ordinal)),
        };
        var config = await ConfigurationAsync(token);

        var result = await aws.Inspector.ReadInventoryAsync(config, Profile, token);
        Assert.Equal(clusterMatches, result.IsComplete);
        Assert.Equal(clusterMatches ? 0 : (int?)null, result.OwnedOccupancy);
        Assert.Empty(result.Tasks);
        string[] expected = clusterMatches ? ["DescribeClusters", "ListTasks", "ListTasks"] : ["DescribeClusters"];
        Assert.Equal(expected, aws.Requests.Select(Operation));
    }

    [Fact]
    public async Task PaginatedInventorySeparatesOwnedForeignAndStoppingTasks()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            ListTasks = query =>
            {
                if (query.GetProperty("desiredStatus").GetString() == "STOPPED")
                {
                    return ListReply([ThirdTask]);
                }
                return query.TryGetProperty("nextToken", out _) ? ListReply([SecondTask]) : ListReply([FirstTask], "second+page=1");
            },
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation), TaskRow(SecondTask),
                TaskRow(ThirdTask, state: "STOPPING", attachedSubnet: Subnet2)),
        };
        var config = await ConfigurationAsync(token);

        var result = await aws.Inspector.ReadInventoryAsync(config, Profile, token);
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.OwnedOccupancy);
        Assert.Equal(2, result.ForeignSubnetOccupancy);
        var owned = Assert.Single(result.Tasks, task => task.IsOwned);
        Assert.Equal(FirstTask, owned.ResourceArn);
        Assert.Equal(FirstAllocation, owned.AllocationId);
        Assert.Equal(Guid.Parse("399f3aad-0c23-fa25-f7c6-42b75438f741"), owned.Incarnation);
        Assert.Equal(Subnet1, Assert.Single(owned.AttachedSubnetIds));
        var stopping = Assert.Single(result.Tasks, task => task.State == FargateTaskState.Stopping);
        Assert.False(stopping.IsOwned);

        var requests = aws.Requests;
        string[] expectedOperations = ["DescribeClusters", "ListTasks", "ListTasks", "ListTasks", "DescribeTasks"];
        Assert.Equal(expectedOperations, requests.Select(Operation));
        Assert.Equal("second+page=1", Body(requests[2]).GetProperty("nextToken").GetString());
        Assert.All(requests.Skip(1), request => Assert.Equal(Cluster, Body(request).GetProperty("cluster").GetString()));
        var described = Body(requests[4]);
        Assert.Equal(new[] { ThirdTask, SecondTask, FirstTask }, described.GetProperty("tasks").EnumerateArray().Select(arn => arn.GetString()));
        Assert.Equal("TAGS", Assert.Single(described.GetProperty("include").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task StartedByRecoveryPreservesOpaquePaginationAndUsesNoOtherFilter()
    {
        var token = TestContext.Current.CancellationToken;
        var row = TaskRow(FirstTask, FirstAllocation);
        row["overrides"] = JsonNode.Parse("""{"containerOverrides":[{"name":"runner"}]}""");
        using var aws = new FargateInspectionFixture
        {
            ListTasks = query => query.TryGetProperty("desiredStatus", out _) || query.TryGetProperty("nextToken", out _)
                ? ListReply([FirstTask]) : ListReply([], "opaque+2"),
            DescribeTasks = _ => TasksReply(row),
        };
        var config = await ConfigurationAsync(token);
        var result = await aws.Inspector.ReadAllocationAsync(config, Profile, FirstAllocation, token);

        Assert.True(result.IsComplete);
        Assert.False(result.IsProfileComplete);
        Assert.Null(result.OwnedOccupancy);
        Assert.Null(result.ForeignSubnetOccupancy);
        Assert.Equal(FirstAllocation, Assert.Single(result.Tasks).AllocationId);
        var planning = result.ToPlanningInventory(new FargateHeadroom(5, 5, 5));
        Assert.False(planning.IsComplete);
        Assert.Null(planning.BackendLaunchHeadroom);
        var pages = aws.Requests.Where(request => Operation(request) == "ListTasks").Select(Body).ToArray();
        Assert.Equal(3, pages.Length);
        Assert.All(pages[..2], page =>
        {
            Assert.Equal(Cluster, page.GetProperty("cluster").GetString());
            Assert.Equal("ss-00112233445566778899aabbccddeeff", page.GetProperty("startedBy").GetString());
        });
        Assert.Equal(2, pages[0].EnumerateObject().Count());
        Assert.Equal(3, pages[1].EnumerateObject().Count());
        Assert.Equal("opaque+2", pages[1].GetProperty("nextToken").GetString());
        Assert.Equal(2, pages[2].EnumerateObject().Count());
        Assert.Equal(Cluster, pages[2].GetProperty("cluster").GetString());
        Assert.Equal("STOPPED", pages[2].GetProperty("desiredStatus").GetString());
        var describe = Assert.Single(aws.Requests, request => Operation(request) == "DescribeTasks");
        Assert.Equal(FirstTask, Assert.Single(Body(describe).GetProperty("tasks").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task AllocationRecoveryFindsTaskThatAlreadyStoppedAndIgnoresUnrelatedTasks()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            ListTasks = query =>
            {
                if (query.TryGetProperty("startedBy", out _))
                {
                    return ListReply([]);
                }
                return query.TryGetProperty("nextToken", out _) ? ListReply([FirstTask]) : ListReply([SecondTask, ThirdTask], "history+2");
            },
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation, "STOPPED", attachedSubnet: null),
                TaskRow(SecondTask, state: "STOPPED"), TaskRow(ThirdTask, OtherAllocation, "STOPPING")),
        };
        var config = await ConfigurationAsync(token);

        var result = await aws.Inspector.ReadAllocationAsync(config, Profile, FirstAllocation, token);

        Assert.True(result.IsComplete);
        Assert.False(result.IsProfileComplete);
        Assert.Null(result.OwnedOccupancy);
        var stopped = Assert.Single(result.Tasks);
        Assert.Equal(FirstTask, stopped.ResourceArn);
        Assert.Equal(FirstAllocation, stopped.AllocationId);
        Assert.Equal(FargateTaskState.Stopped, stopped.State);
        Assert.True(stopped.IsOwned);
        var pages = aws.Requests.Where(request => Operation(request) == "ListTasks").Select(Body).ToArray();
        Assert.Equal(3, pages.Length);
        Assert.Equal(FargateProfileInspector.StartedBy(FirstAllocation), pages[0].GetProperty("startedBy").GetString());
        Assert.False(pages[0].TryGetProperty("desiredStatus", out _));
        Assert.All(pages[1..], page =>
        {
            Assert.Equal(Cluster, page.GetProperty("cluster").GetString());
            Assert.Equal("STOPPED", page.GetProperty("desiredStatus").GetString());
            Assert.False(page.TryGetProperty("startedBy", out _));
        });
        Assert.Equal("history+2", pages[2].GetProperty("nextToken").GetString());
    }

    [Theory]
    [InlineData("duplicate-list")]
    [InlineData("missing-description")]
    [InlineData("wrong-allocation")]
    [InlineData("wrong-started-by")]
    public async Task AllocationRecoveryPreservesIncompleteOrConflictingStoppedEvidence(string fault)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture
        {
            ListTasks = query =>
            {
                if (query.TryGetProperty("startedBy", out _))
                {
                    return ListReply([]);
                }
                return fault switch
                {
                    "duplicate-list" => query.TryGetProperty("nextToken", out _) ? ListReply([FirstTask]) : ListReply([FirstTask], "next"),
                    "missing-description" => ListReply([FirstTask, SecondTask]),
                    _ => ListReply([FirstTask]),
                };
            },
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, fault == "wrong-allocation" ? OtherAllocation : FirstAllocation, "STOPPED",
                startedBy: fault == "wrong-started-by" ? "different" : FargateProfileInspector.StartedBy(FirstAllocation))),
        };
        var config = await ConfigurationAsync(token);

        var result = await aws.Inspector.ReadAllocationAsync(config, Profile, FirstAllocation, token);

        Assert.Equal(FargateInventoryStatus.Incomplete, result.Status);
        Assert.False(result.IsProfileComplete);
        var stopped = Assert.Single(result.Tasks);
        Assert.Equal(FirstTask, stopped.ResourceArn);
        Assert.Equal(fault is "duplicate-list" or "missing-description", stopped.IsOwned);
    }

    [Fact]
    public async Task EmptyAllocationSearchCannotEstablishProfileHeadroom()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture();
        var config = await ConfigurationAsync(token);
        var preflight = Assert.IsType<FargateResolvedTask>((await aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token)).Task);
        var result = await aws.Inspector.ReadAllocationAsync(config, Profile, FirstAllocation, token);
        var quota = new FargateQuotaEvidence(Account, Region, Now, Now.AddMinutes(1), 1024, 0);

        Assert.True(result.IsComplete);
        Assert.Empty(result.Tasks);
        Assert.Equal(new FargateHeadroom(null, null, null), aws.Inspector.EstimateHeadroom(preflight, result, quota, []));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("list-missing")]
    [InlineData("list-loop")]
    [InlineData("wrong-started-by")]
    [InlineData("exec")]
    [InlineData("missing-protocol")]
    [InlineData("wrong-protocol")]
    [InlineData("command-override")]
    [InlineData("environment-override")]
    [InlineData("task-cpu-override")]
    [InlineData("container-memory-override")]
    [InlineData("task-role-override")]
    [InlineData("other-container-override")]
    public async Task IncompleteOrUnsafeTaskEvidenceCannotEstablishOwnership(string fault)
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture();
        var config = await ConfigurationAsync(token);
        var returned = TaskRow(FirstTask, FirstAllocation, state: fault == "partial" ? "STOPPED" : "RUNNING",
            enableExec: fault == "exec", startedBy: fault == "wrong-started-by" ? "unrelated" : null);
        switch (fault)
        {
            case "missing-protocol":
            case "wrong-protocol":
                var tags = returned["tags"]!.AsArray();
                var protocol = Assert.Single(tags, tag => tag!["key"]!.GetValue<string>() == RunnerOwnershipTags.BootstrapProtocol);
                if (fault == "missing-protocol")
                {
                    Assert.True(tags.Remove(protocol));
                }
                else
                {
                    protocol!["value"] = "v2";
                }
                break;
            case "command-override":
                returned["overrides"] = JsonNode.Parse("""{"containerOverrides":[{"name":"runner","command":["different-command"]}]}""");
                break;
            case "environment-override":
                returned["overrides"] = JsonNode.Parse("""{"containerOverrides":[{"name":"runner","environment":[{"name":"TOKEN","value":"synthetic"}]}]}""");
                break;
            case "task-cpu-override":
                returned["overrides"] = JsonNode.Parse("""{"cpu":"2048"}""");
                break;
            case "container-memory-override":
                returned["overrides"] = JsonNode.Parse("""{"containerOverrides":[{"name":"runner","memory":1024}]}""");
                break;
            case "task-role-override":
                returned["overrides"] = JsonNode.Parse("""{"taskRoleArn":"arn:aws:iam::111122223333:role/different"}""");
                break;
            case "other-container-override":
                returned["overrides"] = JsonNode.Parse("""{"containerOverrides":[{"name":"different"}]}""");
                break;
        }
        aws.ListTasks = query => query.TryGetProperty("desiredStatus", out _) ? ListReply([]) : fault switch
        {
            "list-missing" => "{}",
            "list-loop" => ListReply([FirstTask], "same"),
            "partial" => ListReply([FirstTask, SecondTask]),
            _ => ListReply([FirstTask]),
        };
        aws.DescribeTasks = _ => fault == "partial"
            ? new JsonObject
            {
                ["tasks"] = new JsonArray(returned.DeepClone()),
                ["failures"] = new JsonArray(new JsonObject { ["arn"] = SecondTask, ["reason"] = "MISSING" }),
            }.ToJsonString()
            : TasksReply(returned);

        var result = await aws.Inspector.ReadAllocationAsync(config, Profile, FirstAllocation, token);
        Assert.Equal(FargateInventoryStatus.Incomplete, result.Status);
        Assert.Null(result.OwnedOccupancy);
        if (fault == "partial")
        {
            Assert.Equal(FargateTaskState.Stopped, Assert.Single(result.Tasks).State);
            Assert.DoesNotContain(result.Tasks, task => task.ResourceArn == SecondTask);
        }
        if (fault is not ("partial" or "list-missing" or "list-loop"))
        {
            Assert.False(Assert.Single(result.Tasks).IsOwned);
        }
        if (fault == "list-loop")
        {
            Assert.Equal(2, aws.Requests.Count(request => Operation(request) == "ListTasks" && Body(request).TryGetProperty("startedBy", out _)));
        }
    }

    [Fact]
    public async Task DescribeTasksBatchesAtOneHundredWithoutLosingCompleteForeignInventory()
    {
        var token = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(1, 101).Select(value => FormattableString.Invariant($"{TaskPrefix}{value:x32}")).ToArray();
        using var aws = new FargateInspectionFixture
        {
            ListTasks = page =>
            {
                if (page.GetProperty("desiredStatus").GetString() == "STOPPED")
                {
                    return ListReply([]);
                }
                return page.TryGetProperty("nextToken", out _) ? ListReply(ids[100..]) : ListReply(ids[..100], "next");
            },
            DescribeTasks = body => TasksReply(body.GetProperty("tasks").EnumerateArray()
                .Select(item => TaskRow(item.GetString()!)).ToArray()),
        };
        var config = await ConfigurationAsync(token);

        var result = await aws.Inspector.ReadInventoryAsync(config, Profile, token);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.OwnedOccupancy);
        Assert.Equal(101, result.ForeignSubnetOccupancy);
        var batches = aws.Requests.Where(request => Operation(request) == "DescribeTasks").Select(Body).ToArray();
        int[] expectedBatchSizes = [100, 1];
        Assert.Equal(expectedBatchSizes, batches.Select(body => body.GetProperty("tasks").GetArrayLength()));
        Assert.Equal(ids, batches.SelectMany(body => body.GetProperty("tasks").EnumerateArray()).Select(arn => arn.GetString()));
    }

    [Fact]
    public async Task HeadroomSubtractsUnattachedPendingOnceAndLeavesForeignTasksOutsideProfileSlots()
    {
        var token = TestContext.Current.CancellationToken;
        var pending = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f001");
        using var aws = new FargateInspectionFixture
        {
            ListTasks = query => query.GetProperty("desiredStatus").GetString() == "STOPPED" ? ListReply([]) :
                ListReply([FirstTask, SecondTask, ThirdTask]),
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation), TaskRow(SecondTask, pending, state: "PENDING", attachedSubnet: null),
                TaskRow(ThirdTask)),
        };
        var config = await ConfigurationAsync(token);
        var inventory = await aws.Inspector.ReadInventoryAsync(config, Profile, token);
        aws.UtcNow = Now.AddSeconds(1);
        var preflight = Assert.IsType<FargateResolvedTask>((await aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token)).Task);
        var quota = new FargateQuotaEvidence(Account, Region, aws.UtcNow, Now.AddMinutes(5), 4096, 1024);
        FargateLaunchReservation[] outstanding =
        [
            new(FirstAllocation, 1024, [Subnet1, Subnet2], FirstTask),
            new(pending, 1024, [Subnet1, Subnet2], SecondTask),
        ];

        var headroom = aws.Inspector.EstimateHeadroom(preflight, inventory, quota, outstanding);
        Assert.Equal(new FargateHeadroom(6, 2, 2), headroom);
        Assert.Equal(1, inventory.ForeignSubnetOccupancy);
        var planning = inventory.ToPlanningInventory(headroom);
        Assert.Equal(4, planning.BackendCapacityCeiling);
        Assert.Collection(planning.Slots.OrderBy(slot => slot.Id, StringComparer.Ordinal),
            slot => Assert.Equal(RunnerSlotState.Starting, slot.State), slot => Assert.Equal(RunnerSlotState.Unknown, slot.State));

        var older = preflight with { SubnetSampleStartedAt = Now.AddSeconds(-1) };
        Assert.Equal(new FargateHeadroom(5, 1, 1), aws.Inspector.EstimateHeadroom(
            older, inventory, quota with { ObservedAt = Now.AddSeconds(-1) }, outstanding));
    }

    [Fact]
    public async Task AttachmentObservedDuringInventoryStillReservesFromEarlierSamples()
    {
        var token = TestContext.Current.CancellationToken;
        var pendingDescribe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDescribe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingGroups = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGroups = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var aws = new FargateInspectionFixture
        {
            Ip1 = 1,
            Ip2 = 0,
            ListTasks = query => query.GetProperty("desiredStatus").GetString() == "STOPPED" ? ListReply([]) : ListReply([FirstTask]),
            DescribeTasks = _ => TasksReply(TaskRow(FirstTask, FirstAllocation)),
            BeforeRespondAsync = (request, cancellation) =>
            {
                if (Operation(request) == "DescribeTasks")
                {
                    pendingDescribe.TrySetResult();
                    return releaseDescribe.Task.WaitAsync(cancellation);
                }
                if (Operation(request) == "DescribeSecurityGroups")
                {
                    pendingGroups.TrySetResult();
                    return releaseGroups.Task.WaitAsync(cancellation);
                }
                return Task.CompletedTask;
            },
        };
        var config = await ConfigurationAsync(token);
        var readInventory = aws.Inspector.ReadInventoryAsync(config, Profile, token);
        try
        {
            await pendingDescribe.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            aws.UtcNow = Now.AddSeconds(1);
            var quota = new FargateQuotaEvidence(Account, Region, aws.UtcNow, Now.AddMinutes(5), 1024, 0);
            var readPreflight = aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token);
            await pendingGroups.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            aws.UtcNow = Now.AddSeconds(2);
            releaseDescribe.TrySetResult();
            var inventory = await readInventory;
            aws.UtcNow = Now.AddSeconds(3);
            releaseGroups.TrySetResult();
            var task = Assert.IsType<FargateResolvedTask>((await readPreflight).Task);

            Assert.Equal(Now, inventory.ObservedAt);
            Assert.Equal(Now.AddSeconds(2), Assert.Single(inventory.Tasks).AttachmentObservedAt);
            Assert.Equal(Now.AddSeconds(1), task.SubnetSampleStartedAt);
            Assert.Equal(Now.AddSeconds(3), task.ObservedAt);
            FargateLaunchReservation[] reservations = [new(FirstAllocation, 1024, [Subnet1], FirstTask)];
            Assert.Equal(new FargateHeadroom(0, 0, 0), aws.Inspector.EstimateHeadroom(task, inventory, quota, reservations));

            aws.UtcNow = Now.AddSeconds(4);
            var newer = Assert.IsType<FargateResolvedTask>((await aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token)).Task);
            var newerQuota = quota with { ObservedAt = aws.UtcNow };
            Assert.Equal(new FargateHeadroom(1, 1, 1), aws.Inspector.EstimateHeadroom(newer, inventory, newerQuota, reservations));
        }
        finally
        {
            releaseDescribe.TrySetResult();
            releaseGroups.TrySetResult();
        }
    }

    [Fact]
    public async Task QuotaAndSubnetHeadroomPreserveUnknownAndVerifiedZero()
    {
        var token = TestContext.Current.CancellationToken;
        using var aws = new FargateInspectionFixture { Ip1 = 0, Ip2 = 0 };
        var config = await ConfigurationAsync(token);
        var inventory = await aws.Inspector.ReadInventoryAsync(config, Profile, token);
        var knownZero = Assert.IsType<FargateResolvedTask>((await aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token)).Task);
        var quota = new FargateQuotaEvidence(Account, Region, Now, Now.AddMinutes(5), 0, 0);

        Assert.Equal(new FargateHeadroom(0, 0, 0), aws.Inspector.EstimateHeadroom(knownZero, inventory, quota, []));
        Assert.Equal(new FargateHeadroom(0, null, null), aws.Inspector.EstimateHeadroom(knownZero, inventory, null, []));
        Assert.Equal(new FargateHeadroom(0, null, null), aws.Inspector.EstimateHeadroom(knownZero, inventory, quota with { ExpiresAt = Now }, []));
        Assert.Equal(new FargateHeadroom(0, null, null), aws.Inspector.EstimateHeadroom(knownZero, inventory, quota with { Account = "999900001111" }, []));
        aws.Ip1 = null;
        var missingIp = Assert.IsType<FargateResolvedTask>((await aws.Inspector.ResolvePreflightAsync(config, Profile, Approval, token)).Task);
        Assert.Null(missingIp.Subnets[0].AvailableIpAddresses);
        Assert.Equal(new FargateHeadroom(null, 0, null), aws.Inspector.EstimateHeadroom(missingIp, inventory, quota, []));
        Assert.Equal(new FargateHeadroom(null, 0, null), aws.Inspector.EstimateHeadroom(
            knownZero with { SubnetSampleStartedAt = Now.AddMinutes(-11) }, inventory, quota, []));
    }
}
