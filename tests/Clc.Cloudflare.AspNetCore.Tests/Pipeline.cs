using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Clc.Cloudflare.AspNetCore.Tests;

internal static class Pipeline
{
    internal const string Connector = "192.0.2.42";
    internal const string Visitor = "198.51.100.23";

    internal static WebApplication Build(bool? enabled = true, string[]? networks = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var config = new Dictionary<string, string?>();
        if (enabled.HasValue)
        {
            config["Cloudflare:Enabled"] = enabled.ToString();
        }
        var cidrs = networks ?? ["192.0.2.0/24", "2001:db8:1::/48"];
        for (int i = 0; i < cidrs.Length; i++)
        {
            config[$"Cloudflare:TrustedNetworks:{i}"] = cidrs[i];
        }
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddClcCloudflare(builder.Configuration);
        configure?.Invoke(builder);
        return builder.Build();
    }

    internal static async Task StartAsync(WebApplication app, Action<WebApplication>? downstream = null)
    {
        app.UseClcCloudflare();
        downstream?.Invoke(app);
        app.Run(context =>
        {
            context.Items["ObservedAddress"] = context.Connection.RemoteIpAddress?.ToString();
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        await app.StartAsync();
    }

    internal static Task<HttpContext> SendAsync(WebApplication app, string? peer, StringValues visitor = default,
        Action<HttpContext>? configure = null) =>
        app.GetTestServer().SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
            if (visitor.Count > 0)
            {
                context.Request.Headers["CF-Connecting-IP"] = visitor;
            }
            configure?.Invoke(context);
        });
}
