using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knightfall.ScaleSet.Aws.Core.Configuration;

internal static partial class RuntimeConfigurationSemantics
{
    private const string UnicodeResource = "Knightfall.ScaleSet.Aws.Core.Configuration.CaseFolding-17.0.0.txt";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly FrozenDictionary<int, string> CaseMappings = LoadCaseMappings();

    internal static void RejectDeferredGitHubConfig(JsonElement config)
    {
        JsonElement spec = OptionalProperty(config, "spec");
        RejectAuthFields(config, "");
        RejectAuthFields(spec, "/spec");

        JsonElement github = OptionalProperty(spec, "github");
        if (github.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in github.EnumerateObject())
            {
                string path = ChildPath("/spec/github", property.Name);
                JsonElement connection = property.Value;
                RejectAuthFields(connection, path, rejectInline: true);
                RejectAuthFields(OptionalProperty(connection, "auth"), path + "/auth", rejectInline: true);
                JsonElement scope = OptionalProperty(connection, "scopeURL");
                if (scope.ValueKind == JsonValueKind.String)
                {
                    ParseGitHubScope(scope.GetString()!, path + "/scopeURL");
                }
            }
        }

        JsonElement profiles = OptionalProperty(spec, "profiles");
        if (profiles.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement profile in profiles.EnumerateArray())
            {
                RejectAuthFields(OptionalProperty(profile, "github"),
                    ChildPath("/spec/profiles", index++) + "/github", rejectInline: true);
            }
        }
    }

    internal static void Validate(JsonElement config)
    {
        JsonElement spec = config.GetProperty("spec");
        JsonElement hosting = spec.GetProperty("controller").GetProperty("hosting");
        JsonElement networks = spec.GetProperty("networks");
        JsonElement clusters = spec.GetProperty("ecsClusters");
        RequireReference(networks, Text(hosting, "network"), "/spec/controller/hosting/network");
        RequireReference(clusters, Text(hosting, "cluster"), "/spec/controller/hosting/cluster");
        var aws = new AwsScope(Text(spec.GetProperty("aws"), "account"), Text(spec.GetProperty("aws"), "region"));
        ValidateAwsResources(spec, aws);

        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in spec.GetProperty("github").EnumerateObject())
        {
            string path = ChildPath("/spec/github", property.Name);
            var scope = ParseGitHubScope(Text(property.Value, "scopeURL"), path + "/scopeURL");
            if (scope.Enterprise != (Text(property.Value.GetProperty("auth"), "type") == "enterprise-pat"))
            {
                throw Invalid(path + "/auth/type",
                    "Use enterprise-pat for enterprise targets and github-app for organizations and repositories.");
            }
            scopes.Add(property.Name, scope.Identity);
        }

        JsonElement profiles = spec.GetProperty("profiles");
        if (!profiles.EnumerateArray().Any(profile => Text(profile, "state") != "disabled"))
        {
            throw Invalid("/spec/profiles", "At least one profile must be active or draining.");
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        var labelsByScope = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var copilotGroups = new Dictionary<(string Scope, string Group), bool>();
        var profileBindings = new HashSet<(string Scope, string Group, string ScaleSet)>();
        var pinnedSets = new HashSet<(string Scope, long ScaleSetId)>();
        int profileIndex = 0;

        foreach (JsonElement profile in profiles.EnumerateArray())
        {
            string path = ChildPath("/spec/profiles", profileIndex++);
            RequireUnique(profileIds, Text(profile, "id"), path + "/id", "Profile IDs must be unique.");
            ValidateProfile(profile, networks, clusters, aws, path);

            JsonElement routing = profile.GetProperty("github");
            if (!scopes.TryGetValue(Text(routing, "connection"), out string? scope))
            {
                throw Invalid(path + "/github/connection", "The named GitHub connection does not exist.");
            }

            string groupName = Fold(Text(routing, "runnerGroup"));
            string scaleSetName = Fold(Text(routing, "scaleSet"));
            var group = (scope, groupName);
            bool isCopilotProfile = Text(profile, "purpose") == "copilot-cloud-agent";
            if (copilotGroups.TryGetValue(group, out bool isCopilotGroup) && isCopilotGroup != isCopilotProfile)
            {
                throw Invalid(path + "/github/runnerGroup", "Copilot cloud agent profiles require a dedicated runner group.");
            }
            copilotGroups[group] = isCopilotProfile;

            string[] labels = routing.GetProperty("labels").EnumerateArray().Select(label => Fold(label.GetString()!)).ToArray();
            if (!labels.Contains(scaleSetName, StringComparer.Ordinal))
            {
                throw Invalid(path + "/github/labels", "Include the scale-set name in its managed labels.");
            }
            if (!labelsByScope.TryGetValue(scope, out HashSet<string>? usedLabels))
            {
                usedLabels = new HashSet<string>(StringComparer.Ordinal);
                labelsByScope.Add(scope, usedLabels);
            }
            foreach (string label in labels)
            {
                RequireUnique(usedLabels, label, path + "/github/labels", "Managed labels must be disjoint within a GitHub scope.");
            }

            long? pinnedId = ValidatePinnedScaleSet(routing, path);
            if (Text(profile, "state") != "disabled")
            {
                RequireUnique(profileBindings, (scope, groupName, scaleSetName), path + "/github/scaleSet",
                    "Active and draining profiles must have distinct scale-set bindings.");
                if (pinnedId is long id)
                {
                    RequireUnique(pinnedSets, (scope, id), path + "/github/scaleSetId",
                        "Active and draining profiles must have distinct pinned scale-set IDs within a scope.");
                }
            }
        }
    }

    private static long? ValidatePinnedScaleSet(JsonElement routing, string path)
    {
        if (!routing.TryGetProperty("scaleSetId", out JsonElement scaleSetId))
        {
            return null;
        }
        if (!long.TryParse(scaleSetId.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long id))
        {
            throw Invalid(path + "/github/scaleSetId", "Scale-set IDs must fit in a signed 64-bit integer.");
        }
        if (Text(routing, "registration") == "create-if-missing")
        {
            throw Invalid(path + "/github/registration", "A pinned scale-set ID requires existing registration.");
        }
        return id;
    }

    private static void ValidateProfile(JsonElement profile, JsonElement networks, JsonElement clusters, AwsScope aws, string path)
    {
        JsonElement capacity = profile.GetProperty("capacity");
        if (!capacity.GetProperty("idleReserve").TryGetInt32(out int reserve) ||
            !capacity.GetProperty("maxRunners").TryGetInt32(out int maximum))
        {
            throw new RuntimeConfigurationException("schema", path + "/capacity", "Capacity values must be exact JSON integers.");
        }
        if (reserve > maximum)
        {
            throw Invalid(path + "/capacity/idleReserve", "Idle reserve cannot exceed maximum capacity.");
        }

        JsonElement provisioner = profile.GetProperty("provisioner");
        JsonElement platform = profile.GetProperty("platform");
        string purpose = Text(profile, "purpose");
        bool fargate = Text(provisioner, "type") == "fargate";
        if ((purpose == "fargate-compatible") != fargate)
        {
            throw Invalid(path + "/purpose", "Choose a purpose compatible with the runner provisioner.");
        }

        bool linux = Text(platform, "os") == "linux";
        bool x64 = Text(platform, "arch") == "x64";
        if ((fargate && !linux) || (!linux && !x64) || (purpose == "copilot-cloud-agent" && (!linux || !x64)))
        {
            throw Invalid(path + "/platform", "This runner purpose and provisioner require a supported OS and architecture.");
        }

        string prefix = path + "/provisioner";
        RequireReference(networks, Text(provisioner, "network"), prefix + "/network");
        RequireArn(Text(provisioner, "bootstrapRoleArn"), "iam", aws, prefix + "/bootstrapRoleArn");
        if (fargate)
        {
            RequireReference(clusters, Text(provisioner, "cluster"), prefix + "/cluster");
            RequireArn(Text(provisioner, "taskDefinitionArn"), "ecs", aws, prefix + "/taskDefinitionArn");
            RequireArn(Text(provisioner, "executionRoleArn"), "iam", aws, prefix + "/executionRoleArn");
        }
        else
        {
            RequireArn(Text(provisioner, "instanceProfileArn"), "iam", aws, prefix + "/instanceProfileArn");
        }
    }

    private static void ValidateAwsResources(JsonElement spec, AwsScope aws)
    {
        string image = Text(spec.GetProperty("controller"), "image");
        string domain = aws.Region.StartsWith("cn-", StringComparison.Ordinal) ? "amazonaws.com.cn" : "amazonaws.com";
        if (!image.StartsWith($"{aws.Account}.dkr.ecr.{aws.Region}.{domain}/", StringComparison.Ordinal))
        {
            throw Invalid("/spec/controller/image", "The ECR registry must match the deployment account and Region.");
        }

        foreach (JsonProperty cluster in spec.GetProperty("ecsClusters").EnumerateObject())
        {
            if (Text(cluster.Value, "type") == "import")
            {
                RequireArn(Text(cluster.Value, "arn"), "ecs", aws, ChildPath("/spec/ecsClusters", cluster.Name) + "/arn");
            }
        }
        foreach (JsonProperty connection in spec.GetProperty("github").EnumerateObject())
        {
            RequireArn(Text(connection.Value.GetProperty("auth"), "secretArn"), "secretsmanager", aws,
                ChildPath("/spec/github", connection.Name) + "/auth/secretArn");
        }
        foreach (JsonProperty network in spec.GetProperty("networks").EnumerateObject())
        {
            var subnetIds = new HashSet<string>(StringComparer.Ordinal);
            string networkPath = ChildPath("/spec/networks", network.Name);
            int index = 0;
            foreach (JsonElement subnet in network.Value.GetProperty("subnets").EnumerateArray())
            {
                string path = ChildPath(networkPath + "/subnets", index++);
                RequireUnique(subnetIds, Text(subnet, "id"), path + "/id", "Subnet IDs must be unique within a network.");
                string zone = Text(subnet, "az");
                if (!zone.StartsWith(aws.Region, StringComparison.Ordinal) || !ZoneSuffix().IsMatch(zone.AsSpan(aws.Region.Length)))
                {
                    throw Invalid(path + "/az", "The subnet Availability Zone must belong to the deployment Region.");
                }
            }
        }
    }

    private static void RequireArn(string arn, string service, AwsScope aws, string path)
    {
        string[] parts = arn.Split(':', 6);
        string region = service == "iam" ? "" : aws.Region;
        if (parts[1] != Partition(aws.Region) || parts[2] != service || parts[3] != region || parts[4] != aws.Account)
        {
            throw Invalid(path, "The ARN must match the deployment partition, account, and applicable Region.");
        }
    }

    private static string Partition(string region)
    {
        if (region.StartsWith("cn-", StringComparison.Ordinal))
        {
            return "aws-cn";
        }
        if (region.StartsWith("us-gov-", StringComparison.Ordinal))
        {
            return "aws-us-gov";
        }
        if (region.StartsWith("us-isob-", StringComparison.Ordinal))
        {
            return "aws-iso-b";
        }
        if (region.StartsWith("us-isof-", StringComparison.Ordinal))
        {
            return "aws-iso-f";
        }
        if (region.StartsWith("us-iso-", StringComparison.Ordinal))
        {
            return "aws-iso";
        }
        if (region.StartsWith("eu-isoe-", StringComparison.Ordinal))
        {
            return "aws-iso-e";
        }
        return region.StartsWith("eusc-", StringComparison.Ordinal) ? "aws-eusc" : "aws";
    }

    private static (string Identity, bool Enterprise) ParseGitHubScope(string value, string path)
    {
        Match raw = GitHubScopeUrl().Match(value);
        if (!raw.Success || !DnsAuthority().IsMatch(raw.Groups[1].Value) ||
            value.AsSpan().IndexOfAny('\\', '?', '#') >= 0 || value.Any(char.IsWhiteSpace) ||
            value.Contains('\uFEFF'))
        {
            throw Invalid(path, "Use an HTTPS GitHub scope URL without credentials, query, or fragment.");
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? target) || target.Scheme != Uri.UriSchemeHttps)
        {
            throw Invalid(path, "Use a valid absolute GitHub scope URL.");
        }
        if (target.Port != 443 || target.UserInfo.Length != 0)
        {
            throw Invalid(path, "Use the standard HTTPS port with no URL credentials.");
        }

        string host = target.Host.ToLowerInvariant();
        if (host == "www.github.com")
        {
            host = "github.com";
        }
        if (host != "github.com" && !HostedGitHub().IsMatch(host))
        {
            throw new RuntimeConfigurationException("ghes-unsupported", path,
                "Custom GitHub Enterprise Server hosts are deferred in scaleset.NET #20.");
        }

        string[] segments = raw.Groups[2].Value.Trim('/').Split('/');
        if (segments.Length > 2)
        {
            throw Invalid(path, "Identify a GitHub organization, repository, or enterprise.");
        }
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = DecodeScopeSegment(segments[index], path);
            if (segment.Length == 0 || segment is "." or ".." ||
                segment.AsSpan().IndexOfAny('/', '\\') >= 0 || segment.Any(char.IsControl))
            {
                throw Invalid(path, "GitHub scope path segments must be non-empty and contain no separators or control characters.");
            }
            segments[index] = Fold(segment);
        }
        return (host + "/" + string.Join('/', segments), segments.Length == 2 && segments[0] == "enterprises");
    }

    private static string DecodeScopeSegment(string escaped, string path)
    {
        if (!escaped.Contains('%'))
        {
            return escaped;
        }

        var decoded = new StringBuilder(escaped.Length);
        var buffer = new byte[escaped.Length / 3];
        int position = 0;
        while (position < escaped.Length)
        {
            if (escaped[position] != '%')
            {
                decoded.Append(escaped[position++]);
                continue;
            }

            int count = 0;
            do
            {
                if (position + 2 >= escaped.Length ||
                    !Uri.IsHexDigit(escaped[position + 1]) || !Uri.IsHexDigit(escaped[position + 2]))
                {
                    throw Invalid(path, "Use valid URL encoding in GitHub scope path segments.");
                }
                buffer[count++] = byte.Parse(escaped.AsSpan(position + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                position += 3;
            }
            while (position < escaped.Length && escaped[position] == '%');

            try
            {
                decoded.Append(StrictUtf8.GetString(buffer, 0, count));
            }
            catch (DecoderFallbackException)
            {
                throw Invalid(path, "Use valid URL encoding in GitHub scope path segments.");
            }
        }
        return decoded.ToString();
    }

    private static void RejectAuthFields(JsonElement value, string path, bool rejectInline = false)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (JsonProperty property in value.EnumerateObject())
        {
            string name = property.Name;
            if (name is "caBundlePath" or "clientCertificatePath" or "clientCertificate")
            {
                throw new RuntimeConfigurationException("custom-ca-unsupported", ChildPath(path, name),
                    "Custom root CA and mutual TLS settings are deferred in scaleset.NET #21.");
            }
            if (rejectInline && name is "token" or "personalAccessToken" or "pat" or "accessToken" or
                "privateKey" or "clientId" or "installationId")
            {
                throw new RuntimeConfigurationException("inline-credentials-unsupported", ChildPath(path, name),
                    "Reference a Secrets Manager ARN for GitHub authentication.");
            }
        }
    }

    private static FrozenDictionary<int, string> LoadCaseMappings()
    {
        using Stream resource = typeof(RuntimeConfigurationSemantics).Assembly.GetManifestResourceStream(UnicodeResource)
            ?? throw new InvalidOperationException("The Unicode case-folding data is missing.");
        using var reader = new StreamReader(resource, StrictUtf8);
        var mappings = new Dictionary<int, string>();
        while (reader.ReadLine() is string line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] fields = line.Split(';', 4);
            if (fields[1].Trim() is "C" or "S")
            {
                int source = int.Parse(fields[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int target = int.Parse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                mappings.Add(source, char.ConvertFromUtf32(target));
            }
        }
        return mappings.ToFrozenDictionary();
    }

    private static string Fold(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (Rune rune in value.EnumerateRunes())
        {
            result.Append(CaseMappings.TryGetValue(rune.Value, out string? folded) ? folded : rune.ToString());
        }
        return result.ToString();
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;

    private static JsonElement OptionalProperty(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : default;

    private static void RequireReference(JsonElement entries, string name, string path)
    {
        if (!entries.TryGetProperty(name, out _))
        {
            throw Invalid(path, "The named configuration entry does not exist.");
        }
    }

    private static void RequireUnique<T>(HashSet<T> values, T value, string path, string message)
    {
        if (!values.Add(value))
        {
            throw Invalid(path, message);
        }
    }

    private static string ChildPath(string path, int index) => path + "/" + index.ToString(CultureInfo.InvariantCulture);

    private static string ChildPath(string path, string name) =>
        path + "/" + name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static RuntimeConfigurationException Invalid(string path, string message) => new("semantic", path, message);

    [GeneratedRegex(@"\Ahttps://([^/]+)(/.*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubScopeUrl();

    [GeneratedRegex(@"\A[A-Za-z0-9.-]+(?::[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DnsAuthority();

    [GeneratedRegex(@"\A(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+ghe\.com\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostedGitHub();

    [GeneratedRegex(@"\A(?:[a-z]|-[a-z0-9-]+[0-9][a-z])\z", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneSuffix();

    private sealed record AwsScope(string Account, string Region);
}
