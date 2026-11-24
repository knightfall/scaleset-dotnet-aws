using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knightfall.ScaleSet.Aws.Handoff;
using Knightfall.ScaleSet.Aws.RunnerBootstrap;
using Knightfall.ScaleSet.Aws.RunnerBootstrap.Linux;

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
using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));

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
    printf 'offline-jit-sentinel\n                          not a ready signal\n'
    printf '2000-01-01 00:00:00Z: Listening for Jobs\n'
    printf '2000-01-01 00:00:01Z: Listening for Jobs\n'
    """.ReplaceLineEndings("\n"));
File.SetUnixFileMode(executable, Private | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
    UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
var boundary = LinuxRunnerBoundary.Verify(settings);

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
using var plaintext = key.Open(envelope, identity, TimeProvider.System);
var readyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var observations = new List<(long RunnerId, bool Exited)>();
var execution = RunnerBootstrap.RunOneJobAsync(new LinuxOneJobRunner(boundary), plaintext, key.Hello, binding.OfferId,
    async (observation, token) =>
    {
        observations.Add((observation.RunnerId, observation.Exited));
        if (!observation.Exited)
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
    Directory.EnumerateFiles(runtime, ".*credentials*").Any() || File.Exists(Path.Combine(runtime, ".runner")))
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

Console.WriteLine("EC2 Linux launch smoke passed.");
