using System.Net;
using Vinette.Api.Mail.Images;

namespace Vinette.Api.Tests;

/// <summary>Which addresses a picture may be fetched from: public ones, however the address is written.</summary>
public sealed class AddressPolicyTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")] // just below 172.16/12
    [InlineData("172.32.0.1")] // just above it
    [InlineData("100.63.255.255")] // just below carrier-grade NAT
    [InlineData("100.128.0.1")] // just above it
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:81b::200e")]
    [InlineData("::ffff:8.8.8.8")] // mapped from a public address
    [InlineData("64:ff9b::808:808")] // NAT64 of a public address
    [InlineData("2002:808:808::1")] // 6to4 of a public address
    public void Public_Addresses_Are_Allowed(string address) =>
        Assert.True(AddressPolicy.IsPublic(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("169.254.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("100.64.0.1")] // carrier-grade NAT
    [InlineData("100.127.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")] // documentation
    [InlineData("198.18.0.1")] // benchmarking
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("224.0.0.1")] // multicast
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")] // reserved
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")] // link-local
    [InlineData("fc00::1")] // unique local
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")] // multicast
    [InlineData("::ffff:127.0.0.1")] // mapped loopback
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::127.0.0.1")] // IPv4-compatible
    [InlineData("64:ff9b::7f00:1")] // NAT64 of loopback
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 of the metadata address
    [InlineData("2002:7f00:1::1")] // 6to4 of loopback
    [InlineData("2002:a00:1::1")] // 6to4 of 10.0.0.1
    [InlineData("2001::1")] // Teredo
    [InlineData("2001:db8::1")] // documentation
    public void Private_Local_And_Special_Addresses_Are_Refused(string address) =>
        Assert.False(AddressPolicy.IsPublic(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("2130706433")] // 127.0.0.1 as one number
    [InlineData("0x7f000001")] // in hex
    [InlineData("017700000001")] // in octal
    [InlineData("127.1")] // shortened
    public void An_Address_Written_As_A_Number_Is_Judged_By_What_It_Is(string written)
    {
        // The address has to be parsed the way the network stack does, so that it is the same one that is checked.
        Assert.True(Uri.TryCreate($"http://{written}/", UriKind.Absolute, out var uri));
        Assert.True(IPAddress.TryParse(uri.Host, out var parsed));
        Assert.False(AddressPolicy.IsPublic(parsed));
    }
}
