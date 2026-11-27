param([string]$PackageVersion = '0.0.0-local')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path $PSScriptRoot -Parent
$id = 'Clc.Cloudflare.AspNetCore'
$feed = Join-Path $repository 'artifacts/packages'
$package = Join-Path $feed "$id.$PackageVersion.nupkg"
$url = 'https://github.com/clcdpc/clc-cloudflare-aspnetcore'

$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in @("$id.nuspec", "lib/net10.0/$id.dll", 'README.md', 'LICENSE')) {
        if (!$archive.GetEntry($entry)) { throw "Missing package entry $entry" }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry("$id.nuspec").Open())
    try { $nuspec = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    $meta = $nuspec.package.metadata
    if ($meta.id -cne $id -or $meta.version -cne $PackageVersion -or $meta.readme -cne 'README.md' -or
        $meta.authors -cne 'Central Library Consortium' -or $meta.license.type -ne 'expression' -or
        $meta.license.InnerText -cne 'MIT' -or $meta.projectUrl -cne $url -or $meta.repository.url -cne $url -or
        $meta.repository.type -ne 'git' -or [string]::IsNullOrWhiteSpace($meta.description)) {
        throw 'Unexpected NuGet package metadata.'
    }
    if ($meta.frameworkReferences.group.targetFramework -cne 'net10.0' -or
        $meta.frameworkReferences.group.frameworkReference.name -cne 'Microsoft.AspNetCore.App' -or
        $meta.SelectNodes(".//*[local-name()='dependency']").Count -ne 0) {
        throw 'Expected only the ASP.NET Core shared framework.'
    }
}
finally { $archive.Dispose() }

# Restore into a fresh cache from the verified package directory alone.
$run = Join-Path ([IO.Path]::GetTempPath()) ("Clc.Cloudflare.Consumer-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
try {
    $project = Join-Path $run 'Consumer.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="$id" Version="[$PackageVersion]" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project
    @'
using Clc.Cloudflare.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Configuration["Cloudflare:Enabled"] = "true";
builder.Configuration["Cloudflare:TrustedNetworks:0"] = "127.0.0.1/32";
builder.Services.AddClcCloudflare(builder.Configuration);
await using var app = builder.Build();
app.UseClcCloudflare();
app.MapGet("/", (HttpContext context) => context.Connection.RemoteIpAddress?.ToString());
await app.StartAsync();
try
{
    using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
    client.DefaultRequestHeaders.Add("CF-Connecting-IP", "198.51.100.23");
    if (await client.GetStringAsync(app.Urls.Single()) != "198.51.100.23")
        throw new InvalidOperationException("Packaged consumer did not observe the visitor address.");
}
finally
{
    await app.StopAsync();
}
'@ | Set-Content -LiteralPath (Join-Path $run 'Program.cs')
    dotnet restore $project --source $feed --packages (Join-Path $run 'packages')
    if ($LASTEXITCODE -ne 0) { throw 'Consumer restore failed.' }
    dotnet run --project $project -c Release --no-restore --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'Packed web consumer failed.' }
}
finally {
    $resolved = [IO.Path]::GetFullPath($run)
    $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempRoot -or
        [IO.Path]::GetFileName($resolved) -notmatch '^Clc\.Cloudflare\.Consumer-[0-9a-f]{32}$') {
        throw 'Unexpected temporary cleanup target.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output "Verified $id $PackageVersion metadata and packed web consumer."
