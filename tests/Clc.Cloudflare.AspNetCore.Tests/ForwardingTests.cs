using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Clc.Cloudflare.AspNetCore.Tests;

[TestClass]
public sealed class ForwardingTests
{
    [TestMethod]
    [DataRow(Pipeline.Connector, Pipeline.Visitor, Pipeline.Visitor)]
    [DataRow("2001:db8:1::7", "2001:db8::5", "2001:db8::5")]
    [DataRow("::ffff:192.0.2.42", Pipeline.Visitor, Pipeline.Visitor)]
    [DataRow(Pipeline.Connector, "::ffff:198.51.100.23", Pipeline.Visitor)]
    [DataRow("203.0.113.5", Pipeline.Visitor, "203.0.113.5")]
    [DataRow("127.0.0.1", Pipeline.Visitor, "127.0.0.1")]
    [DataRow("10.1.2.3", Pipeline.Visitor, "10.1.2.3")]
    [DataRow(null, Pipeline.Visitor, null)]
    [DataRow(Pipeline.Connector, null, Pipeline.Connector)]
    public async Task DownstreamSeesExpectedAddress(string? peer, string? visitor, string? expected)
    {
        await using var app = Pipeline.Build();
        await Pipeline.StartAsync(app);
        var response = await Pipeline.SendAsync(app, peer, visitor);
        Assert.AreEqual(204, response.Response.StatusCode);
        Assert.AreEqual(expected, response.Items["ObservedAddress"]);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(" 198.51.100.23")]
    [DataRow("198.51.100.23\t")]
    [DataRow("192.0.2.1,198.51.100.23")]
    [DataRow("invalid, 198.51.100.23")]
    [DataRow("198.51.100.23,")]
    [DataRow("198.51.100.23:443")]
    [DataRow("[2001:db8::1]:443")]
    [DataRow("[2001:db8::1]")]
    [DataRow("fe80::1%2")]
    [DataRow("192.0.2.999")]
    [DataRow("not-an-address")]
    [DataRow("127.1")]
    [DataRow("0300.0.2.1")]
    [DataRow("0xC0000201")]
    [DataRow("3221225985")]
    public async Task RejectsEntireInvalidValue(string visitor)
    {
        await using var app = Pipeline.Build();
        await Pipeline.StartAsync(app);
        var response = await Pipeline.SendAsync(app, Pipeline.Connector, visitor);
        Assert.AreEqual(Pipeline.Connector, response.Items["ObservedAddress"]);
    }

    [TestMethod]
    public async Task RepeatedHeaderFieldsEvenWhenIdenticalPreservePeer()
    {
        await using var app = Pipeline.Build();
        await Pipeline.StartAsync(app);
        var response = await Pipeline.SendAsync(app, Pipeline.Connector,
            new StringValues([Pipeline.Visitor, Pipeline.Visitor]));
        Assert.AreEqual(Pipeline.Connector, response.Items["ObservedAddress"]);
    }

    [TestMethod]
    public async Task MappedConnectorNetworkAcceptsNativeConnector()
    {
        await using var app = Pipeline.Build(networks: ["::ffff:192.0.2.42/128"]);
        await Pipeline.StartAsync(app);
        var response = await Pipeline.SendAsync(app, Pipeline.Connector, Pipeline.Visitor);
        Assert.AreEqual(Pipeline.Visitor, response.Items["ObservedAddress"]);
    }

    [TestMethod]
    [DataRow(null, Pipeline.Connector)]
    [DataRow(Pipeline.Visitor, Pipeline.Visitor)]
    public async Task UnrelatedForwardedHeadersHaveNoEffect(string? visitor, string expected)
    {
        await using var app = Pipeline.Build();
        await Pipeline.StartAsync(app);
        var response = await Pipeline.SendAsync(app, Pipeline.Connector, visitor, context =>
        {
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("origin.example");
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
            context.Request.Headers["X-Forwarded-Proto"] = "http";
            context.Request.Headers["X-Forwarded-Host"] = "other.example";
        });
        Assert.AreEqual(expected, response.Items["ObservedAddress"]);
        Assert.AreEqual("https", response.Request.Scheme);
        Assert.AreEqual("origin.example", response.Request.Host.Value);
    }

    [TestMethod]
    public async Task HostingForwarderCanRestoreConnectorAndSchemeFirst()
    {
        await using var app = Pipeline.Build(configure: builder => builder.Services.Configure<ForwardedHeadersOptions>(
            options => options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto));
        // Model IIS Integration's loopback-to-connector hop with the host's default options.
        app.UseForwardedHeaders();
        await Pipeline.StartAsync(app);
        var response = await Pipeline.SendAsync(app, "127.0.0.1", Pipeline.Visitor, context =>
        {
            context.Request.Headers["X-Forwarded-For"] = Pipeline.Connector;
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        Assert.AreEqual(Pipeline.Visitor, response.Items["ObservedAddress"]);
        Assert.AreEqual("https", response.Request.Scheme);
    }

    [TestMethod]
    public async Task RateLimiterPartitionsRequestsByResolvedVisitor()
    {
        await using var app = Pipeline.Build(configure: builder => builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress!.ToString(), _ =>
                    new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromHours(1) }));
        }));
        await Pipeline.StartAsync(app, downstream => downstream.UseRateLimiter());
        var first = await Pipeline.SendAsync(app, Pipeline.Connector, Pipeline.Visitor);
        var repeated = await Pipeline.SendAsync(app, Pipeline.Connector, Pipeline.Visitor);
        var other = await Pipeline.SendAsync(app, Pipeline.Connector, "198.51.100.24");
        Assert.AreEqual(204, first.Response.StatusCode);
        Assert.AreEqual(429, repeated.Response.StatusCode);
        Assert.AreEqual(204, other.Response.StatusCode);
    }
}
