# Clc.Cloudflare.AspNetCore

Use `HttpContext.Connection.RemoteIpAddress` for the visitor IP in .NET 10 applications behind Cloudflare Zero Trust Tunnels. The package configures ASP.NET Core's forwarded-header middleware for `CF-Connecting-IP`. It is disabled by default.

## Setup

```sh
dotnet add package Clc.Cloudflare.AspNetCore
```

Configure the connector addresses in `appsettings.json`. Replace these example addresses with the sources observed at your origin.

```json
{
  "Cloudflare": {
    "Enabled": true,
    "TrustedNetworks": [
      "192.0.2.10/32",
      "192.0.2.11/32"
    ]
  }
}
```

```csharp
using Clc.Cloudflare.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddClcCloudflare(builder.Configuration);

var app = builder.Build();
app.UseClcCloudflare();

// Register rate limiting, auditing and other visitor-IP consumers here.
app.MapGet("/ip", (HttpContext context) => context.Connection.RemoteIpAddress?.ToString());
app.Run();
```

## Choosing trusted networks

CLC hosts its `cloudflared` connectors on machines separate from IIS. With IIS in-process, the source is normally the connector that opened the connection to IIS. Out-of-process, IIS Integration normally restores the connector address and scheme from the IIS-to-Kestrel hop before the application pipeline runs. Leave that integration in place. Verify the address visible immediately before `UseClcCloudflare()`, and keep earlier forwarding restricted to authenticated or network-isolated proxy hops. [ASP.NET Core proxy hosting](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)

Prefer a `/32` per IPv4 connector or a `/128` per IPv6 connector. Use a subnet only when every source in it can be trusted to supply visitor addresses. Trusting all RFC 1918 ranges would let any reachable internal machine in those ranges impersonate a visitor. Cloudflare's published edge ranges describe a different hop. Loopback, private addresses and edge ranges receive no implicit trust.

When enabled, at least one valid CIDR is required at startup. Blank entries, malformed networks and trust-all prefixes (including the IPv4-mapped equivalent) fail validation. Restart the app after changing connector settings.

## Request behavior

A trusted peer can forward one bare IPv4 or IPv6 literal. Multiple fields, comma-separated lists, surrounding whitespace visible to the application, ports, zone IDs and ambiguous IPv4 spellings are ignored. Missing or invalid headers, untrusted peers and disabled forwarding leave the connection address intact. A valid IPv4-mapped visitor is exposed as IPv4. The package processes only the client address and preserves the host and scheme.

Warnings contain the category and immediate peer address. Event 1201 indicates an untrusted source sent the visitor header. Event 1202 indicates a trusted connector sent `CF-Ray` or `CF-Visitor` without the visitor header. Event 1203 indicates a trusted connector sent an invalid visitor header. Each pipeline logs at most one warning per category per minute. Visitor-header values are omitted and requests continue normally.

## Build and release

Run `dotnet build -c Release`, `dotnet test --solution Clc.Cloudflare.AspNetCore.slnx -c Release`, then `dotnet pack src -c Release -o artifacts/packages` and `pwsh ./scripts/Test-Package.ps1` to verify the archive and run a separate package-backed web application.

GitHub Actions validates pull requests and pushes to `master` on Windows and Linux. Local builds use `0.0.0-local`, and untagged CI uses `0.0.0-ci.<run>.<attempt>`. Only an explicit release tag such as `v1.2.3` or `v1.2.3-beta.1` can publish. The publish job verifies the tested artifact's checksum and exchanges a GitHub OIDC token for a short-lived NuGet key.

Before the first release, confirm package ID ownership for `clcdpc`, create the GitHub `nuget-production` environment restricted to `v*` tags, and configure a NuGet trusted publishing policy for `clcdpc/clc-cloudflare-aspnetcore`, workflow `build-test-publish.yaml`, and that environment. Authorize the CLC NuGet login `mfields` for the package and policy. [NuGet trusted publishing setup](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
