using Microsoft.Extensions.Options;

namespace Clc.Cloudflare.AspNetCore.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("192.0.2.1")]
    [DataRow("example.org/32")]
    [DataRow("192.0.2.0/33")]
    [DataRow("2001:db8::/129")]
    [DataRow("192.0.2.0/-1")]
    [DataRow("192.0.2.0/")]
    [DataRow("0.0.0.0/0")]
    [DataRow("::/0")]
    [DataRow("::ffff:0.0.0.0/96")]
    [DataRow("0300.0.2.0/24")]
    public async Task EnabledConfigurationFailsAtHostStartup(string? invalid)
    {
        await using var app = Pipeline.Build(networks: invalid is null ? [] : ["192.0.2.0/24", invalid]);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());
        StringAssert.Contains(error.Message, "Cloudflare:TrustedNetworks");
    }

    [TestMethod]
    public void ForwardingUsesOnlyDistinctConfiguredNetworks()
    {
        var options = CloudflareExtensions.CreateForwardingOptions(
            ["192.0.2.0/24", "::ffff:192.0.2.0/120", "2001:db8::/32", "2001:0db8::/32"]);
        CollectionAssert.AreEquivalent(new[] { "192.0.2.0/24", "2001:db8::/32" },
            options.KnownIPNetworks.Select(network => network.ToString()).ToArray());
        Assert.IsEmpty(options.KnownProxies);
        Assert.AreEqual(1, options.ForwardLimit);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(false)]
    public async Task DisabledInProductionIgnoresHeadersAndUnusedNetworks(bool? enabled)
    {
        await using var app = Pipeline.Build(enabled, networks: ["invalid"]);
        await Pipeline.StartAsync(app);
        var response = await Pipeline.SendAsync(app, Pipeline.Connector, Pipeline.Visitor);
        Assert.AreEqual(Pipeline.Connector, response.Items["ObservedAddress"]);
        Assert.AreEqual(Pipeline.Visitor, response.Request.Headers["CF-Connecting-IP"].ToString());
    }
}
