using System;
using System.IO;
using System.Net;
using Microsoft.AspNetCore.Http;
using VnMiningServer.Access;
using VnMiningServer.Tests.Support;

namespace VnMiningServer.Tests;

public sealed class AccessTests : IDisposable
{
    private const string Key = "a-key-of-sufficient-length";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vn-mining-access-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static DefaultHttpContext Request(string? peer, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return context;
    }

    [Fact]
    public void A_key_from_the_environment_is_used_and_nothing_is_written()
    {
        var key = ApiAccess.ReadOrMakeKey("  " + Key + " ", _folder, out var made);

        Assert.Equal(Key, key);
        Assert.False(made);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void Without_one_a_key_is_made_once_and_kept_in_a_file_only_the_servers_user_may_read()
    {
        var first = ApiAccess.ReadOrMakeKey(null, _folder, out var made);
        var path = Path.Combine(_folder, ApiAccess.KeyFileName);

        Assert.True(made);
        Assert.True(first.Length >= 40);
        Assert.Equal(first, File.ReadAllText(path).Trim());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        Assert.Equal(first, ApiAccess.ReadOrMakeKey(string.Empty, _folder, out made));
        Assert.False(made);
    }

    [Fact]
    public void Two_servers_make_different_keys()
    {
        var one = ApiAccess.ReadOrMakeKey(null, Path.Combine(_folder, "one"), out _);
        var two = ApiAccess.ReadOrMakeKey(null, Path.Combine(_folder, "two"), out _);

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void A_key_too_short_to_be_one_is_not_taken()
    {
        Assert.Throws<ArgumentException>(() => new ApiAccess("short", _time));
    }

    [Theory]
    [InlineData(Key, true)]
    [InlineData(Key + " ", false)]
    [InlineData("a-key-of-sufficient-lengtH", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_key_is_the_key(string? candidate, bool expected)
    {
        Assert.Equal(expected, new ApiAccess(Key, _time).IsKey(candidate));
    }

    [Fact]
    public void A_session_holds_for_400_days_and_not_a_second_longer()
    {
        var access = new ApiAccess(Key, _time);
        var (value, ends) = access.NewSession();

        Assert.Equal(_time.Now + TimeSpan.FromDays(400), ends);
        Assert.True(access.IsSession(value));
        _time.Advance(TimeSpan.FromDays(400) - TimeSpan.FromSeconds(1));
        Assert.True(access.IsSession(value));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(access.IsSession(value));
    }

    [Fact]
    public void A_session_does_not_hold_the_key_and_ends_with_it()
    {
        var (value, _) = new ApiAccess(Key, _time).NewSession();

        Assert.DoesNotContain(Key, value, StringComparison.Ordinal);
        Assert.False(new ApiAccess("another-key-of-sufficient-length", _time).IsSession(value));
    }

    [Fact]
    public void A_session_someone_changed_or_made_up_does_not_hold()
    {
        var access = new ApiAccess(Key, _time);
        var (value, _) = access.NewSession();
        var parts = value.Split('.');
        var later = (long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) + 86400).ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.False(access.IsSession($"{parts[0]}.{later}.{parts[2]}"));
        Assert.False(access.IsSession($"v2.{parts[1]}.{parts[2]}"));
        Assert.False(access.IsSession(parts[0] + "." + parts[1]));
        Assert.False(access.IsSession("v1.x.y"));
        Assert.False(access.IsSession(string.Empty));
        Assert.False(access.IsSession(null));
    }

    [Fact]
    public void Ten_wrong_keys_in_a_minute_lock_an_address_until_the_minute_is_over()
    {
        var limiter = new FailureLimiter(_time);
        for (var attempt = 0; attempt < FailureLimiter.MaxFailures - 1; attempt++)
        {
            limiter.Failed("203.0.113.9");
        }

        Assert.False(limiter.IsLocked("203.0.113.9", out _));
        limiter.Failed("203.0.113.9");

        Assert.True(limiter.IsLocked("203.0.113.9", out var wait));
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
        Assert.False(limiter.IsLocked("203.0.113.10", out _));

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(limiter.IsLocked("203.0.113.9", out wait));
        Assert.Equal(TimeSpan.FromSeconds(1), wait);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(limiter.IsLocked("203.0.113.9", out _));
    }

    [Fact]
    public void The_count_of_wrong_keys_starts_anew_each_minute()
    {
        var limiter = new FailureLimiter(_time);
        for (var attempt = 0; attempt < 9; attempt++)
        {
            limiter.Failed("203.0.113.9");
        }

        _time.Advance(TimeSpan.FromSeconds(61));
        limiter.Failed("203.0.113.9");

        Assert.False(limiter.IsLocked("203.0.113.9", out _));
    }

    [Fact]
    public void Many_addresses_do_not_fill_the_memory()
    {
        var limiter = new FailureLimiter(_time);
        for (var address = 0; address < FailureLimiter.MaxAddresses * 2; address++)
        {
            limiter.Failed("address-" + address);
        }

        // Still counting, for an address that came after the list was emptied.
        for (var attempt = 0; attempt < FailureLimiter.MaxFailures; attempt++)
        {
            limiter.Failed("203.0.113.9");
        }

        Assert.True(limiter.IsLocked("203.0.113.9", out _));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.20", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("203.0.113.9", false)]
    [InlineData("2001:db8::1", false)]
    public void A_near_address_is_this_machine_or_a_private_network(string address, bool near)
    {
        Assert.Equal(near, ClientAddress.IsNear(IPAddress.Parse(address)));
    }

    [Fact]
    public void A_proxy_on_this_machine_is_believed_about_the_caller()
    {
        Assert.Equal("198.51.100.7", ClientAddress.Of(Request("127.0.0.1", ("CF-Connecting-IP", "198.51.100.7"), ("X-Forwarded-For", "203.0.113.1"))));
        Assert.Equal("203.0.113.1", ClientAddress.Of(Request("127.0.0.1", ("X-Forwarded-For", "203.0.113.1, 10.0.0.1"))));
        Assert.Equal("127.0.0.1", ClientAddress.Of(Request("127.0.0.1")));
        Assert.Equal("127.0.0.1", ClientAddress.Of(Request("127.0.0.1", ("X-Forwarded-For", "not an address"))));
    }

    [Fact]
    public void A_caller_from_the_internet_cannot_name_another_address()
    {
        var request = Request("203.0.113.9", ("CF-Connecting-IP", "198.51.100.7"), ("X-Forwarded-For", "198.51.100.8"), ("X-Forwarded-Proto", "https"));

        Assert.Equal("203.0.113.9", ClientAddress.Of(request));
        Assert.False(ClientAddress.IsHttps(request));
    }

    [Fact]
    public void A_proxy_on_this_machine_is_believed_about_https()
    {
        Assert.True(ClientAddress.IsHttps(Request("127.0.0.1", ("X-Forwarded-Proto", "https"))));
        Assert.False(ClientAddress.IsHttps(Request("127.0.0.1", ("X-Forwarded-Proto", "http"))));
        Assert.False(ClientAddress.IsHttps(Request("127.0.0.1")));
    }

    [Theory]
    [InlineData("GET", "https://elsewhere.example", true)]
    [InlineData("POST", null, true)]
    [InlineData("POST", "https://vn.example.org", true)]
    [InlineData("POST", "https://VN.example.org", true)]
    [InlineData("POST", "https://elsewhere.example", false)]
    [InlineData("POST", "http://vn.example.org:8080", false)]
    [InlineData("DELETE", "null", false)]
    public void The_cookie_counts_for_what_the_page_itself_sends(string method, string? origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Host = new HostString("vn.example.org");
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        Assert.Equal(expected, KeyAuthenticationHandler.SameSite(context.Request));
    }
}
