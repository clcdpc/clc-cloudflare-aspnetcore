using IPNetwork = System.Net.IPNetwork;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Clc.Cloudflare.AspNetCore;

public static class CloudflareExtensions
{
    public static IServiceCollection AddClcCloudflare(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CloudflareOptions>()
            .Bind(configuration.GetSection("Cloudflare"))
            .Validate(options => !options.Enabled || options.TrustedNetworks is { Length: > 0 },
                "Cloudflare:TrustedNetworks requires at least one connector CIDR when Cloudflare is enabled.")
            .Validate(options => !options.Enabled || options.TrustedNetworks is null
                || options.TrustedNetworks.All(value => CloudflareAddresses.TryParseNetwork(value, out _)),
                "Each Cloudflare:TrustedNetworks entry must be a valid IPv4 or IPv6 CIDR with a nonzero network prefix.")
            .ValidateOnStart();

        return services;
    }

    public static IApplicationBuilder UseClcCloudflare(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<CloudflareOptions>>().Value;
        if (!settings.Enabled)
        {
            return app;
        }

        var forwarding = CreateForwardingOptions(settings.TrustedNetworks);
        var logger = app.ApplicationServices.GetRequiredService<ILogger<CloudflareHeaderFilter>>();
        var filter = new CloudflareHeaderFilter(forwarding.KnownIPNetworks, logger);
        return app.UseWhen(filter.ShouldForward, branch => branch.UseForwardedHeaders(forwarding));
    }

    // Use private options so IIS Integration keeps its own proxy and scheme settings.
    internal static ForwardedHeadersOptions CreateForwardingOptions(IEnumerable<string> cidrs)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
            ForwardedForHeaderName = CloudflareHeaderFilter.HeaderName,
            ForwardLimit = 1
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        var networks = new HashSet<IPNetwork>();
        foreach (var cidr in cidrs)
        {
            if (!CloudflareAddresses.TryParseNetwork(cidr, out var network))
            {
                throw new InvalidOperationException($"Invalid Cloudflare:TrustedNetworks entry '{cidr}'.");
            }
            if (networks.Add(network))
            {
                options.KnownIPNetworks.Add(network);
            }
        }

        return options;
    }
}
