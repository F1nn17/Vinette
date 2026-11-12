using System.Net;
using System.Net.Sockets;

namespace Vinette.Api.Mail.Images;

/// <summary>
/// Which addresses the application may fetch pictures from: public ones only. Everything that leads into a private
/// network, to the machine itself, to cloud metadata services (169.254.169.254) or is not an address of a single host is
/// refused. Addresses that embed an IPv4 address (IPv4-mapped, NAT64, 6to4, Teredo, IPv4-compatible) are judged by it.
/// </summary>
public static class AddressPolicy
{
    /// <summary>Whether <paramref name="address"/> is a public unicast address.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IsPublicV6(address);
        }

        return address.AddressFamily == AddressFamily.InterNetwork && IsPublicV4(address.GetAddressBytes());
    }

    private static bool IsPublicV4(ReadOnlySpan<byte> b)
    {
        var (a, c, d) = (b[0], b[1], b[2]);
        return !(a == 0 // "this network", 0.0.0.0
            || a == 10 // private
            || (a == 100 && c is >= 64 and <= 127) // carrier-grade NAT
            || a == 127 // loopback
            || (a == 169 && c == 254) // link-local, cloud metadata
            || (a == 172 && c is >= 16 and <= 31) // private
            || (a == 192 && c == 0 && d == 0) // IETF protocol assignments
            || (a == 192 && c == 0 && d == 2) // documentation
            || (a == 192 && c == 88 && d == 99) // 6to4 relay
            || (a == 192 && c == 168) // private
            || (a == 198 && c is 18 or 19) // benchmarking
            || (a == 198 && c == 51 && d == 100) // documentation
            || (a == 203 && c == 0 && d == 113) // documentation
            || a >= 224); // multicast, reserved, broadcast
    }

    private static bool IsPublicV6(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicV4(address.MapToIPv4().GetAddressBytes());
        }

        var b = address.GetAddressBytes();

        // ::/96 (this includes :: and ::1) and 64:ff9b::/96 (NAT64) carry an IPv4 address in the last four bytes.
        if (b.Take(12).All(x => x == 0)
            || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0)))
        {
            return IsPublicV4(b.AsSpan(12, 4));
        }

        // Only global unicast space, 2000::/3, can be a public host: link-local, unique local, multicast, "this host" and
        // everything else that is not allotted are outside it.
        if ((b[0] & 0xe0) != 0x20)
        {
            return false;
        }

        // 2002::/16 (6to4) carries the IPv4 address in bytes 2 to 5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicV4(b.AsSpan(2, 4));
        }

        if (b[0] == 0x20 && b[1] == 0x01)
        {
            return !(b[2] == 0x00 && b[3] == 0x00 // Teredo
                || (b[2] == 0x0d && b[3] == 0xb8) // documentation
                || (b[2] == 0x00 && b[3] == 0x02) // benchmarking
                || (b[2] == 0x00 && (b[3] & 0xf0) is 0x10 or 0x20)); // ORCHID
        }

        return true;
    }
}
