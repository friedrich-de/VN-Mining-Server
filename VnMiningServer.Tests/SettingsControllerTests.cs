using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VnMiningServer.Api;
using VnMiningServer.Lines;
using VnMiningServer.Settings;
using VnMiningServer.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace VnMiningServer.Tests;

public sealed class SettingsControllerTests : IDisposable
{
    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private SettingsController Controller() => new(_test.Build())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
            }
        }
    };

    private static ProblemDetails Problem(ActionResult? result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    private async Task<SettingsView> PutAsync(SettingsChange change) => (await Controller().PutSettings(change)).Value!;

    private string StoredFile() => File.ReadAllText(Directory.GetFiles(Path.Combine(_test.Scratch, "users")).Single());

    [Fact]
    public async Task A_new_user_has_no_anki_and_no_page_settings()
    {
        var view = (await Controller().GetSettings()).Value!;

        Assert.Equal(AnkiWhere.Off, view.Anki.Where);
        Assert.Equal("http://127.0.0.1:8765", view.Anki.DeviceAddress);
        Assert.False(view.Anki.ServerKeySet);
        Assert.Null(view.Page);
        Assert.Equal(LineStore.DefaultMaxLines, view.Stream.MaxLines);
    }

    [Fact]
    public async Task The_device_key_comes_back_to_its_user_and_is_encrypted_on_disk()
    {
        var view = await PutAsync(new SettingsChange
        {
            Anki = new AnkiChange { Where = AnkiWhere.Device, DeviceAddress = "http://localhost:8766", DeviceKey = " device-key-123 " },
            Page = JsonDocument.Parse("""{"deck":"Mining"}""").RootElement,
        });

        Assert.Equal((AnkiWhere.Device, "http://localhost:8766", "device-key-123"), (view.Anki.Where, view.Anki.DeviceAddress, view.Anki.DeviceKey));
        Assert.Equal("Mining", view.Page!.Value.GetProperty("deck").GetString());
        Assert.DoesNotContain("device-key-123", StoredFile(), StringComparison.Ordinal);

        // A change that leaves things out keeps them.
        view = await PutAsync(new SettingsChange { Anki = new AnkiChange { Where = AnkiWhere.Off } });
        Assert.Equal(("http://localhost:8766", "device-key-123", "Mining"), (view.Anki.DeviceAddress, view.Anki.DeviceKey, view.Page!.Value.GetProperty("deck").GetString()));
    }

    [Fact]
    public async Task The_server_key_never_leaves_the_server()
    {
        _test.SignInAs(administrator: true);

        var view = await PutAsync(new SettingsChange
        {
            Anki = new AnkiChange { Where = AnkiWhere.Server, ServerAddress = "https://anki.example.org/", ServerKey = "server-key-456" },
        });

        Assert.True(view.Anki.ServerKeySet);
        Assert.DoesNotContain("server-key-456", JsonSerializer.Serialize(view), StringComparison.Ordinal);
        Assert.DoesNotContain("server-key-456", StoredFile(), StringComparison.Ordinal);
        Assert.Equal("server-key-456", _test.Keys.Reveal(_test.Settings.Load(TestServices.UserId).ServerKey));

        // Left out it stays; empty takes it away.
        Assert.True((await PutAsync(new SettingsChange { Anki = new AnkiChange { ServerAddress = "https://anki.example.org/v2" } })).Anki.ServerKeySet);
        Assert.False((await PutAsync(new SettingsChange { Anki = new AnkiChange { ServerKey = string.Empty } })).Anki.ServerKeySet);
    }

    [Theory]
    [InlineData("anywhere", null, null)]
    [InlineData(null, "127.0.0.1:8765", null)]
    [InlineData(null, "file:///etc/passwd", null)]
    [InlineData(null, null, "https://user:password@anki.example.org")]
    public async Task What_is_no_choice_or_no_address_is_refused(string? where, string? device, string? server)
    {
        _test.SignInAs(administrator: true);

        var result = await Controller().PutSettings(new SettingsChange { Anki = new AnkiChange { Where = where, DeviceAddress = device, ServerAddress = server } });

        Problem(result.Result, StatusCodes.Status400BadRequest);
        Assert.Equal(AnkiWhere.Off, _test.Settings.Load(TestServices.UserId).Where);
    }

    [Fact]
    public async Task The_pages_own_part_is_an_object_of_a_sensible_size()
    {
        Problem((await Controller().PutSettings(new SettingsChange { Page = JsonDocument.Parse("[1,2]").RootElement })).Result, StatusCodes.Status400BadRequest);

        var large = JsonDocument.Parse(JsonSerializer.Serialize(new { text = new string('x', UserSettingsStore.MaxPageBytes) })).RootElement;
        Problem((await Controller().PutSettings(new SettingsChange { Page = large })).Result, StatusCodes.Status400BadRequest);

        var key = new string('k', AnkiAddress.MaxKeyLength + 1);
        Problem((await Controller().PutSettings(new SettingsChange { Anki = new AnkiChange { DeviceKey = key } })).Result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Fewer_lines_to_keep_take_the_oldest_away_at_once()
    {
        for (var number = 1; number <= 60; number++)
        {
            _test.Lines.Add(new NewLine("line " + number, null, null, null), null, null);
        }

        var view = await PutAsync(new SettingsChange { Stream = new StreamSettingsView { MaxLines = 50 } });

        Assert.Equal(50, view.Stream.MaxLines);
        Assert.Equal(50, _test.Shared.MaxLines);
        Assert.Equal(50, _test.Lines.Count());
        Assert.Equal("line 11", _test.Lines.After(0, 1)[0].Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(49)]
    [InlineData(20001)]
    public async Task The_stream_keeps_a_sensible_number_of_lines(int lines)
    {
        var result = await Controller().PutSettings(new SettingsChange { Stream = new StreamSettingsView { MaxLines = lines } });

        Problem(result.Result, StatusCodes.Status400BadRequest);
        Assert.Equal(LineStore.DefaultMaxLines, _test.Shared.MaxLines);
    }

    [Fact]
    public async Task Without_a_user_there_are_no_settings()
    {
        _test.Callers.Caller = null;

        Problem((await Controller().GetSettings()).Result, StatusCodes.Status403Forbidden);
        Problem((await Controller().PutSettings(new SettingsChange())).Result, StatusCodes.Status403Forbidden);
    }
}
