using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Clc.Cloudflare.AspNetCore.Tests;

[TestClass]
public sealed class DiagnosticTests
{
    [TestMethod]
    [DataRow("CF-Ray")]
    [DataRow("CF-Visitor")]
    public async Task WarningsIdentifyOnlyTheCategoryAndImmediatePeer(string marker)
    {
        var logs = new WarningLog();
        await using var app = Pipeline.Build(configure: builder => builder.Logging.AddProvider(logs));
        await Pipeline.StartAsync(app);

        const string untrusted = "203.0.113.7";
        await Pipeline.SendAsync(app, Pipeline.Connector); // Ordinary origin request
        await Pipeline.SendAsync(app, Pipeline.Connector, Pipeline.Visitor);
        Assert.IsEmpty(logs.Entries);

        for (int i = 0; i < 2; i++)
        {
            await Pipeline.SendAsync(app, untrusted, Pipeline.Visitor);
            await Pipeline.SendAsync(app, Pipeline.Connector, configure: ctx => ctx.Request.Headers[marker] = "marker");
            await Pipeline.SendAsync(app, "::ffff:192.0.2.42", Pipeline.Visitor + ", invalid");
        }

        var entries = logs.Entries.ToArray();
        CollectionAssert.AreEqual(new[] { 1201, 1202, 1203 }, entries.Select(entry => entry.Event.Id).ToArray());
        CollectionAssert.AreEqual(new[] { untrusted, Pipeline.Connector, Pipeline.Connector },
            entries.Select(entry => entry.State["ProxySourceAddress"]).ToArray());
        foreach (var entry in entries)
        {
            Assert.AreEqual(entry.Event.Name, entry.State["DiagnosticCategory"]);
            CollectionAssert.AreEquivalent(new[] { "DiagnosticCategory", "ProxySourceAddress", "{OriginalFormat}" },
                entry.State.Keys.ToArray());
            Assert.IsFalse(entry.Message.Contains(Pipeline.Visitor, StringComparison.Ordinal));
            Assert.IsFalse(entry.State.Values.Any(value => value?.Contains(Pipeline.Visitor, StringComparison.Ordinal) == true));
        }
    }

    [TestMethod]
    public async Task ConcurrentDistinctSourcesCannotFloodWarnings()
    {
        var logs = new WarningLog();
        await using var app = Pipeline.Build(configure: builder => builder.Logging.AddProvider(logs));
        await Pipeline.StartAsync(app);
        var responses = await Task.WhenAll(Enumerable.Range(1, 50)
            .Select(i => Pipeline.SendAsync(app, $"203.0.113.{i}", Pipeline.Visitor)));
        Assert.IsTrue(responses.All(context => context.Response.StatusCode == 204));
        Assert.HasCount(1, logs.Entries);
    }

    [TestMethod]
    public async Task FailingLogProviderStillAllowsRequestThrough()
    {
        var logs = new WarningLog(throwOnWarning: true);
        await using var app = Pipeline.Build(configure: builder => builder.Logging.AddProvider(logs));
        await Pipeline.StartAsync(app);
        var response = await Pipeline.SendAsync(app, Pipeline.Connector, "invalid");
        Assert.HasCount(1, logs.Entries);
        Assert.AreEqual(204, response.Response.StatusCode);
        Assert.AreEqual(Pipeline.Connector, response.Items["ObservedAddress"]);
    }

    [TestMethod]
    public async Task DisabledIntegrationDoesNotWarn()
    {
        var logs = new WarningLog();
        await using var app = Pipeline.Build(false, configure: builder => builder.Logging.AddProvider(logs));
        await Pipeline.StartAsync(app);
        await Pipeline.SendAsync(app, "203.0.113.7", Pipeline.Visitor);
        Assert.IsEmpty(logs.Entries);
    }

    private sealed record Entry(EventId Event, string Message, Dictionary<string, string?> State);

    private sealed class WarningLog(bool throwOnWarning = false) : ILoggerProvider, ILogger
    {
        internal ConcurrentQueue<Entry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
            {
                return;
            }
            var fields = (IEnumerable<KeyValuePair<string, object?>>)(object)state!;
            Entries.Enqueue(new Entry(eventId, formatter(state, exception),
                fields.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString())));
            if (throwOnWarning)
            {
                throw new InvalidOperationException("Test log provider failed.");
            }
        }
    }
}
