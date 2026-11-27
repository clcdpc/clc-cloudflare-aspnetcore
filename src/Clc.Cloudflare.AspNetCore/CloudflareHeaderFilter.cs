using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Clc.Cloudflare.AspNetCore;

internal sealed class CloudflareHeaderFilter(IList<IPNetwork> networks, ILogger<CloudflareHeaderFilter> logger)
{
    internal const string HeaderName = "CF-Connecting-IP";
    private static readonly EventId Untrusted = new(1201, "UntrustedCloudflareSource");
    private static readonly EventId Missing = new(1202, "MissingCloudflareVisitorHeader");
    private static readonly EventId Invalid = new(1203, "InvalidCloudflareVisitorHeader");
    private readonly Dictionary<EventId, long> lastWarning = [];

    internal bool ShouldForward(HttpContext context)
    {
        var headers = context.Request.Headers;
        var present = headers.TryGetValue(HeaderName, out var values);
        if (!present && !headers.ContainsKey("CF-Ray") && !headers.ContainsKey("CF-Visitor"))
        {
            return false;
        }

        var peer = context.Connection.RemoteIpAddress;
        if (peer?.IsIPv4MappedToIPv6 == true)
        {
            peer = peer.MapToIPv4();
        }

        if (peer is null || !networks.Any(network => network.Contains(peer)))
        {
            if (present)
            {
                Warn(Untrusted, peer);
            }
            return false;
        }

        if (!present)
        {
            Warn(Missing, peer);
            return false;
        }

        if (values.Count != 1 || !CloudflareAddresses.TryParseLiteral(values[0], out var visitor))
        {
            Warn(Invalid, peer);
            return false;
        }

        if (visitor.IsIPv4MappedToIPv6)
        {
            headers[HeaderName] = visitor.MapToIPv4().ToString();
        }
        return true;
    }

    private void Warn(EventId category, IPAddress? peer)
    {
        lock (lastWarning)
        {
            if (lastWarning.TryGetValue(category, out var previous)
                && Stopwatch.GetElapsedTime(previous) < TimeSpan.FromMinutes(1))
            {
                return;
            }
            lastWarning[category] = Stopwatch.GetTimestamp();
        }

        try
        {
            logger.LogWarning(category, "{DiagnosticCategory} from immediate proxy {ProxySourceAddress}.",
                category.Name, peer?.ToString() ?? "unknown");
        }
        catch (Exception)
        {
            // Diagnostics are best effort. A failing provider must not interrupt the request.
        }
    }
}
