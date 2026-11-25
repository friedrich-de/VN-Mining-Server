using System;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VnMiningServer.Api;
using VnMiningServer.Settings;
using VnMiningServer.Tests.Support;

namespace VnMiningServer.Tests;

public sealed class PageControllerTests : IDisposable
{
    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private PageController Controller(params string[] files) => new(_test.Build(files))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
            }
        }
    };

    [Fact]
    public void The_page_needs_no_key_and_is_locked_down()
    {
        _test.SignOut();
        var controller = Controller("index.html", "assets/app-1a2b3c.js", "sw.js");

        var page = Assert.IsType<FileStreamResult>(controller.GetPage());

        Assert.Single(typeof(PageController).GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false));
        Assert.Equal("text/html; charset=utf-8", page.ContentType);
        Assert.Equal("no-cache", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"].ToString());
        var policy = controller.Response.Headers.ContentSecurityPolicy.ToString();
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self' http://127.0.0.1:*", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_a_hash_in_its_name_is_kept_by_the_browser_for_good()
    {
        var controller = Controller("index.html", "assets/app-1a2b3c.js", "sw.js");

        using var script = Assert.IsType<FileStreamResult>(controller.GetFile("assets/app-1a2b3c.js")).FileStream;
        Assert.Equal("public, max-age=31536000, immutable", controller.Response.Headers.CacheControl.ToString());

        using var worker = Assert.IsType<FileStreamResult>(controller.GetFile("sw.js")).FileStream;
        Assert.Equal("no-cache", controller.Response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("nothing.js")]
    [InlineData("../api-key.txt")]
    [InlineData("Api/Unknown")]
    [InlineData("index.html/")]
    public void Anything_that_is_not_one_of_the_pages_files_is_not_found(string path)
    {
        Assert.IsType<NotFoundResult>(Controller("index.html").GetFile(path));
    }

    [Fact]
    public void A_build_without_its_page_says_so()
    {
        var result = Assert.IsType<ObjectResult>(Controller().GetPage());

        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.Contains("npm run build", Assert.IsType<ProblemDetails>(result.Value).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_servers_own_settings_are_kept_in_a_file_only_its_user_may_read()
    {
        var folder = Path.Combine(_test.Scratch, "data");
        var settings = new FileSharedSettings(folder, NullLogger<FileSharedSettings>.Instance);
        Assert.Equal((string.Empty, 1000), (settings.WordAudioAddress, settings.MaxLines));
        Assert.False(Directory.Exists(folder));

        settings.SetMaxLines(250);
        settings.SetWordAudioAddress("stored");
        settings.SetMaxLines(1_000_000);

        var path = Path.Combine(folder, FileSharedSettings.FileName);
        var again = new FileSharedSettings(folder, NullLogger<FileSharedSettings>.Instance);
        Assert.Equal(("stored", 20000), (again.WordAudioAddress, again.MaxLines));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        File.WriteAllText(path, "{ not json");
        Assert.Equal(1000, new FileSharedSettings(folder, NullLogger<FileSharedSettings>.Instance).MaxLines);
    }

    [Fact]
    public void The_games_settings_file_is_two_lines_a_game_can_read()
    {
        var text = GameSettingsController.Write("https://vn.example.org", "the-key");

        Assert.EndsWith("server = https://vn.example.org\nkey = the-key\n", text, StringComparison.Ordinal);
        Assert.All(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[..^2], row => Assert.StartsWith("#", row, StringComparison.Ordinal));
    }
}
