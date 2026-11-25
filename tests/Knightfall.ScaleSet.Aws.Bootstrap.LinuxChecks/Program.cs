using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Bootstrap.LinuxChecks;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

if (OperatingSystem.IsLinux() && args is ["fargate"])
{
    return await FargateLinuxCheck.RunAsync();
}

if (!OperatingSystem.IsLinux() || !LinuxFiles.IsRoot())
{
    throw new InvalidOperationException("This smoke check must run inside its disposable Linux fixture.");
}

LinuxFiles.DisableCoreDumps();

const string runtime = "/run/scaleset-fixture";
const string privateState = "/var/lib/scaleset-fixture";
const string credentialSentinel = "offline-jit-sentinel";
const string parentSentinel = "offline-parent-credential";
const uint group = 1500;
const uint user = 1500;
const string instanceArn = "arn:aws:ec2:ap-southeast-2:111122223333:instance/i-0123456789abcdef1";
const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(35));

Directory.CreateDirectory(privateState, Private | UnixFileMode.UserExecute);
Directory.CreateDirectory(Path.Combine(runtime, "bin"));
string executable = Path.Combine(runtime, "bin", "Runner.Listener");
File.WriteAllText(executable, """
    #!/bin/sh
    set -eu
    test "$(id -u)" = 1500
    test "$(id -g)" = 1500
    test "$(id -G)" = 1500
    test "$#" = 2
    test "$1" = run
    test "$2" = --once
    test -z "${AWS_SESSION_TOKEN:-}"
    test -z "${ACTIONS_RUNNER_INPUT_JITCONFIG:-}"
    test "${AWS_EC2_METADATA_DISABLED:-}" = true
    grep -Eq '^NoNewPrivs:[[:space:]]+1$' /proc/self/status
    grep -Eq '^CapEff:[[:space:]]+0+$' /proc/self/status
    grep -Eq '^CapBnd:[[:space:]]+0+$' /proc/self/status
    test "$(stat -c "%a" .credentials)" = 600
    test "$(stat -f -c "%T" .credentials)" = tmpfs
    test "$(cat .credentials)" = offline-jit-sentinel
    test ! -r /var/lib/scaleset-fixture/parent-environment
    test "$(awk '/^Uid:/ {print $2}' /proc/1/status)" = 0
    test "$(readlink /proc/self/ns/pid)" != "$(cat fixture-parent-pidns)"
    mode=$(cat fixture-mode)
    /usr/bin/setsid /usr/bin/bash --noprofile --norc -c '
        touch "$1"
        exec -a "$2" /usr/bin/sleep 60
    ' fixture-detached "fixture-detached-$mode" "scaleset-fixture-detached-$mode" &
    while [ ! -f "fixture-detached-$mode" ]; do sleep 0.01; done
    if [ "$mode" = partial ]; then
        rm .runner
        mkdir .runner .runner_migrated
        touch .runner/retained
        ln -s /var/lib/scaleset-fixture/parent-environment .credentials_migrated
        printf 'job-owned fixture' > .credentials_rsaparams
    fi
    printf 'offline-jit-sentinel\n                          not a ready signal\n'
    printf '2000-01-01 00:00:00Z: Listening for Jobs\n'
    printf '2000-01-01 00:00:01Z: Listening for Jobs\n'
    if [ "$mode" = canceled ]; then wait; fi
    """.ReplaceLineEndings("\n"));
File.SetUnixFileMode(executable, Private | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
    UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
File.WriteAllText(Path.Combine(runtime, "fixture-parent-pidns"),
    new FileInfo("/proc/self/ns/pid").LinkTarget ?? throw new InvalidOperationException("Fixture PID namespace is unavailable."));
string digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable)));
string boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
string proofPath = Path.Combine(privateState, "proof.json");
var proof = new LinuxRunnerBoundaryProof(1, boot, runtime, digest, user, group, true, true, true, true);
var settings = new LinuxRunnerSettings(runtime, proofPath, digest, user, group);
File.WriteAllText(proofPath, JsonSerializer.Serialize(proof with { Ipv6MetadataDenied = false }, BootstrapJson.Options));
File.SetUnixFileMode(proofPath, Private);
try
{
    _ = LinuxRunnerBoundary.Verify(settings);
    throw new InvalidOperationException("The unchecked IPv6 boundary was accepted.");
}
catch (IOException)
{
}
File.WriteAllText(proofPath, JsonSerializer.Serialize(proof, BootstrapJson.Options));
if (LinuxRunnerBoundary.Verify(settings).IsolatedProcessTree)
{
    throw new InvalidOperationException("The unqualified mode changed its launcher.");
}
string qualification = new('a', 64);
try
{
    _ = LinuxRunnerBoundary.Verify(settings, qualification);
    throw new InvalidOperationException("The legacy boot proof was accepted for qualified STATUS.");
}
catch (IOException)
{
}
var priorQualification = proof with { StatusQualificationSha256 = qualification };
File.WriteAllText(proofPath, JsonSerializer.Serialize(priorQualification, BootstrapJson.Options));
try
{
    _ = LinuxRunnerBoundary.Verify(settings, qualification);
    throw new InvalidOperationException("The prior qualification was accepted without job teardown proof.");
}
catch (RunnerTeardownQualificationException)
{
}
File.WriteAllText(proofPath, JsonSerializer.Serialize(priorQualification with { JobProcessTeardownVerified = true }, BootstrapJson.Options));
var boundary = LinuxRunnerBoundary.Verify(settings, qualification);

File.WriteAllText(Path.Combine(privateState, "parent-environment"), parentSentinel);
Environment.SetEnvironmentVariable("AWS_SESSION_TOKEN", parentSentinel);
Environment.SetEnvironmentVariable("ACTIONS_RUNNER_INPUT_JITCONFIG", parentSentinel);
var oldProcess = boundary.StartInfo();
if (string.Join(' ', oldProcess.ArgumentList).Contains(credentialSentinel, StringComparison.Ordinal) ||
    oldProcess.Environment.Values.Contains(parentSentinel, StringComparer.Ordinal))
{
    throw new InvalidOperationException("The child inherited private bootstrap material.");
}

var marker = new Ec2SpentMarker(privateState, instanceArn);
if (!marker.TryReserve() || new Ec2SpentMarker(privateState, instanceArn).TryReserve())
{
    throw new InvalidOperationException("A reconstructed supervisor repeated the spent reservation.");
}
string onDiskMarker = Directory.EnumerateFiles(privateState, "*.spent").Single();
if (File.ReadAllText(onDiskMarker) != "v1\n" || File.GetUnixFileMode(onDiskMarker) != Private)
{
    throw new InvalidOperationException("The persistent spent marker was not private and nonsecret.");
}
await Ec2HelloLinuxCheck.VerifySpentMarkerAsync(privateState, new LinuxOneJobRunner(boundary), cancel.Token);

using var key = JitRecipientKey.Create();
var identity = new JitHandoffIdentity("fixture", "test", "github.com/test/test", 12, Guid.NewGuid(), instanceArn, Guid.NewGuid(),
    "AROAFIXTUREIDENTITY0001:i-0123456789abcdef1");
var binding = JitOfferBinding.Create(identity, key.Hello.BootNonce, DateTimeOffset.UtcNow.AddMinutes(1));
string runner = "ss-" + binding.OfferId[..48];
var members = new Dictionary<string, string>(StringComparer.Ordinal)
{
    [".runner"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { AgentId = 17, AgentName = runner, Ephemeral = true, WorkFolder = "_work" })),
    [".credentials"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentialSentinel)),
    [".credentials_rsaparams"] = Convert.ToBase64String("private-rsa-fixture"u8),
};
byte[] jit = Encoding.ASCII.GetBytes(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(members)));
var envelope = JitOfferCryptography.Seal(binding, key.Hello, jit, TimeProvider.System);
SetMode("normal");
IOneJobRunner jobRunner = new LinuxOneJobRunner(boundary);
using var plaintext = key.Open(envelope, identity, TimeProvider.System);
var readyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var observations = new List<(long RunnerId, bool Exited)>();
bool cleanedBeforeExitObservation = false;
var execution = RunnerBootstrap.RunOneJobAsync(jobRunner, plaintext, key.Hello, binding.OfferId,
    async (observation, token) =>
    {
        observations.Add((observation.RunnerId, observation.Exited));
        if (observation.Exited)
        {
            RequireCleanConfig(keepCollision: false);
            RequireNoDetachedProcess("normal");
            cleanedBeforeExitObservation = true;
        }
        else
        {
            readyStarted.TrySetResult();
            await releaseReady.Task.WaitAsync(token);
            throw new IOException("Synthetic readiness status failure.");
        }
    }, TimeProvider.System, cancel.Token);
try
{
    await readyStarted.Task.WaitAsync(cancel.Token);
    // The listener has no work after the two ready lines. Hold status beyond the pipe drain budget.
    await Task.Delay(TimeSpan.FromSeconds(6), cancel.Token);
}
finally
{
    releaseReady.TrySetResult();
}

var completion = await execution;
if (completion is not { RunnerId: 17, ExitCode: 0 } || !observations.SequenceEqual([(17L, false), (17L, true)]) ||
    !cleanedBeforeExitObservation)
{
    throw new InvalidOperationException("The Linux launcher did not report the exact runner exit and erase its volatile configuration.");
}
try
{
    _ = plaintext.Bytes;
    throw new InvalidOperationException("The plaintext lease survived launch.");
}
catch (ObjectDisposedException)
{
}

using var unrelated = Process.Start(new ProcessStartInfo("/usr/bin/setpriv")
{
    UseShellExecute = false,
    ArgumentList = { "--reuid=1500", "--regid=1500", "--clear-groups", "--", "/usr/bin/sleep", "60" },
}) ?? throw new InvalidOperationException("The unrelated fixture process could not start.");
try
{
    await VerifyCancellationAsync();
    await VerifyPartialCleanupAsync();
    if (unrelated.HasExited)
    {
        throw new InvalidOperationException("Qualified cleanup stopped an unrelated process.");
    }
}
finally
{
    if (!unrelated.HasExited)
    {
        unrelated.Kill();
        await unrelated.WaitForExitAsync(cancel.Token);
    }
}

Console.WriteLine("EC2 Linux launch, namespace teardown, credential cleanup and HELLO spent-marker smoke passed.");
return 0;

void SetMode(string mode)
{
    var home = Path.Combine(runtime, "_bootstrap-home");
    if (Directory.Exists(home))
    {
        Directory.Delete(home);
    }
    File.WriteAllText(Path.Combine(runtime, "fixture-mode"), mode);
}

async Task VerifyCancellationAsync()
{
    SetMode("canceled");
    using var lease = key.Open(envelope, identity, TimeProvider.System);
    using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
    var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var canceledObservations = new List<bool>();
    var job = RunnerBootstrap.RunOneJobAsync(jobRunner, lease, key.Hello, binding.OfferId,
        (observation, _) =>
        {
            canceledObservations.Add(observation.Exited);
            running.TrySetResult();
            return Task.CompletedTask;
        }, TimeProvider.System, stopping.Token);
    await running.Task.WaitAsync(cancel.Token);
    await stopping.CancelAsync();
    try
    {
        _ = await job.WaitAsync(TimeSpan.FromSeconds(10), cancel.Token);
        throw new InvalidOperationException("Canceled execution completed as an observed job exit.");
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested && !cancel.IsCancellationRequested)
    {
    }

    if (!canceledObservations.SequenceEqual([false]))
    {
        throw new InvalidOperationException("Canceled execution published an exit observation.");
    }
    RequireCleanConfig(keepCollision: false);
    RequireNoDetachedProcess("canceled");
}

async Task VerifyPartialCleanupAsync()
{
    SetMode("partial");
    using var lease = key.Open(envelope, identity, TimeProvider.System);
    var partialObservations = new List<bool>();
    try
    {
        _ = await RunnerBootstrap.RunOneJobAsync(jobRunner, lease, key.Hello, binding.OfferId,
            (observation, _) =>
            {
                partialObservations.Add(observation.Exited);
                return Task.CompletedTask;
            }, TimeProvider.System, cancel.Token);
        throw new InvalidOperationException("The nonempty credential-directory collision was accepted.");
    }
    catch (RunnerCleanupIncompleteException)
    {
    }
    if (partialObservations.Contains(true))
    {
        throw new InvalidOperationException("Partial cleanup published an exit observation.");
    }
    RequireCleanConfig(keepCollision: true);
    RequireNoDetachedProcess("partial");
}

static void RequireCleanConfig(bool keepCollision)
{
    string[] expected = keepCollision ? [".runner"] : [];
    var actual = Directory.EnumerateFileSystemEntries(runtime, ".*").Select(Path.GetFileName).ToArray();
    if (!actual.SequenceEqual(expected) ||
        (keepCollision && !File.Exists(Path.Combine(runtime, ".runner", "retained"))) ||
        File.ReadAllText(Path.Combine(privateState, "parent-environment")) != parentSentinel)
    {
        throw new InvalidOperationException("Credential cleanup did not preserve the expected trusted-root boundary.");
    }
}

static void RequireNoDetachedProcess(string mode)
{
    string tag = "scaleset-fixture-detached-" + mode + '\0';
    if (!File.Exists(Path.Combine(runtime, "fixture-detached-" + mode)))
    {
        throw new InvalidOperationException("The detached process fixture was never started.");
    }
    foreach (string process in Directory.EnumerateDirectories("/proc").Where(path => int.TryParse(Path.GetFileName(path), out _)))
    {
        try
        {
            if (File.ReadAllText(Path.Combine(process, "cmdline")).StartsWith(tag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The detached job process survived teardown.");
            }
        }
        catch (IOException)
        {
            // A process can disappear between proc enumeration and its command line read.
        }
    }
}
