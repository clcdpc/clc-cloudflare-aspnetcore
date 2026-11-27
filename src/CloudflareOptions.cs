namespace Clc.Cloudflare.AspNetCore;

public sealed class CloudflareOptions
{
    public bool Enabled { get; set; }

    public string[] TrustedNetworks { get; set; } = [];
}
