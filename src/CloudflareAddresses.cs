using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Clc.Cloudflare.AspNetCore;

internal static class CloudflareAddresses
{
    internal static bool TryParseLiteral(ReadOnlySpan<char> text, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        // Limit the input to a bare address, including IPv6 with a dotted IPv4 tail.
        if (text.IsEmpty || text.Length > 45 || text.ContainsAnyExcept("0123456789abcdefABCDEF:.")
            || !IPAddress.TryParse(text, out address))
        {
            return false;
        }

        // IPAddress also accepts historical IPv4 spellings such as "127.1" and octal.
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return text.SequenceEqual(address.ToString());
        }

        return !text.Contains('.') || TryParseLiteral(text[(text.LastIndexOf(':') + 1)..], out _);
    }

    internal static bool TryParseNetwork(string? text, out IPNetwork network)
    {
        network = default;
        var cidr = text.AsSpan();
        var slash = cidr.IndexOf('/');
        if (slash <= 0 || !TryParseLiteral(cidr[..slash], out var address)
            || cidr[(slash + 1)..].ContainsAnyExceptInRange('0', '9')
            || !IPNetwork.TryParse(cidr, out network) || network.PrefixLength == 0)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            // The first 96 bits are the mapping prefix. /96 would trust all IPv4.
            if (network.PrefixLength <= 96)
            {
                return false;
            }

            network = new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
        }

        return true;
    }
}
