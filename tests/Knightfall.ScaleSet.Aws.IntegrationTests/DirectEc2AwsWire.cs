using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Web;
using System.Xml.Linq;
using Amazon.Runtime;

namespace Knightfall.ScaleSet.Aws.IntegrationTests;

/// <summary>Preserves native instance state while supplementing EC2 response fields omitted by MiniStack 1.5.44.</summary>
internal sealed class DirectEc2AwsWire(Uri endpoint, string firstSubnet) : DelegatingHandler(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
})
{
    private readonly ConcurrentQueue<NameValueCollection> _calls = new();
    private readonly ConcurrentDictionary<string, NativeLaunch> _launches = new(StringComparer.Ordinal);
    private int _rejectFirstSubnet = 1;

    internal NameValueCollection[] Calls(string action) => _calls.Where(call => call["Action"] == action).ToArray();

    internal HttpClientFactory ClientFactory => new Factory(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { Scheme: "http", Host: "127.0.0.1" } uri || uri.Authority != endpoint.Authority ||
            request.Method != HttpMethod.Post || request.Content is null)
        {
            throw new InvalidOperationException("Direct EC2 fixture requests must target the exact local MiniStack endpoint.");
        }

        var args = HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync(cancellationToken));
        _calls.Enqueue(args);
        string? action = args["Action"];
        if (action == "RunInstances" && args["SubnetId"] == firstSubnet && Interlocked.Exchange(ref _rejectFirstSubnet, 0) != 0)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""
                    <Response><Errors><Error><Code>InsufficientInstanceCapacity</Code><Message>Fixture AZ has no capacity.</Message></Error></Errors>
                    <RequestID>fixture-az-capacity</RequestID></Response>
                    """, Encoding.UTF8, "text/xml"),
            };
        }

        var native = await base.SendAsync(request, cancellationToken);
        if (!native.IsSuccessStatusCode || action is not ("RunInstances" or "DescribeInstances" or "DescribeImages"))
        {
            return native;
        }

        try
        {
            var document = XDocument.Parse(await native.Content.ReadAsStringAsync(cancellationToken));
            var ns = document.Root!.Name.Namespace;
            if (action == "DescribeImages")
            {
                foreach (var image in document.Descendants(ns + "imagesSet").Elements(ns + "item"))
                {
                    if (image.Element(ns + "imageId")?.Value == DirectEc2Fixture.LinuxImage && image.Element(ns + "platformDetails") is null)
                    {
                        image.Add(new XElement(ns + "platformDetails", "Linux/UNIX"));
                    }
                }
            }
            else if (action == "RunInstances")
            {
                var launch = new NativeLaunch(args["ClientToken"]!, args["LaunchTemplate.LaunchTemplateId"]!, args["LaunchTemplate.Version"]!);
                // Retain only evidence from the exact SDK invocation that produced this native instance ID.
                foreach (var instance in document.Descendants(ns + "instancesSet").Elements(ns + "item"))
                {
                    _launches.TryAdd(instance.Element(ns + "instanceId")!.Value,
                        launch with { OriginalLaunchTime = instance.Element(ns + "launchTime")!.Value });
                }
                return native;
            }
            else
            {
                string? token = args.AllKeys
                    .Where(key => key?.EndsWith(".Name", StringComparison.Ordinal) == true && args[key] == "client-token")
                    .Select(key => args[key![..^5] + ".Value.1"]).SingleOrDefault();
                foreach (var item in document.Descendants(ns + "instancesSet").Elements(ns + "item").ToArray())
                {
                    string id = item.Element(ns + "instanceId")!.Value;
                    if (!_launches.TryGetValue(id, out var launch) || (token is not null && launch.Token != token))
                    {
                        if (token is not null)
                        {
                            item.Remove(); // MiniStack returns all candidates for the unsupported client-token filter.
                        }
                        continue;
                    }
                    item.Add(new XElement(ns + "clientToken", launch.Token));
                    var tags = item.Element(ns + "tagSet")!;
                    tags.Add(
                        Tag(ns, "aws:ec2launchtemplate:id", launch.TemplateId),
                        Tag(ns, "aws:ec2launchtemplate:version", launch.TemplateVersion));
                    item.Add(new XElement(ns + "networkInterfaceSet", new XElement(ns + "item",
                        new XElement(ns + "attachment", new XElement(ns + "deviceIndex", 0),
                            new XElement(ns + "attachTime", launch.OriginalLaunchTime)))));
                }
            }

            var content = new StringContent(document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
            native.Content.Dispose();
            native.Content = content;
            return native;
        }
        catch
        {
            native.Dispose();
            throw;
        }
    }

    private static XElement Tag(XNamespace ns, string key, string value) =>
        new(ns + "item", new XElement(ns + "key", key), new XElement(ns + "value", value));

    private sealed record NativeLaunch(string Token, string TemplateId, string TemplateVersion, string? OriginalLaunchTime = null);

    private sealed class Factory(DirectEc2AwsWire wire) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(wire, disposeHandler: false);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}
