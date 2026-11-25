using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using VnMiningServer.Api;
using VnMiningServer.Lines;
using VnMiningServer.Tests.Support;

namespace VnMiningServer.Tests;

public sealed class LinesControllerTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;

    public void Dispose() => _test.Dispose();

    private LinesController Controller(Stream? body = null, TimeSpan? beat = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider()
        };
        if (body is not null)
        {
            context.Response.Body = body;
        }

        return new LinesController(_test.Build())
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            BeatEvery = beat ?? LinesController.Beat,
        };
    }

    private static ProblemDetails Problem(ActionResult? result, int expectedStatus)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    private static FormFile File(byte[] content, string name) => new(new MemoryStream(content), 0, content.Length, name, name);

    private StreamLine Add(string text, bool voice = true, bool picture = true) =>
        _test.Lines.Add(new NewLine(text, "明日香", null, null), voice ? Sample.Ogg : null, picture ? Sample.Jpeg : null);

    [Fact]
    public void Everything_about_the_lines_needs_the_key()
    {
        Assert.Single(typeof(LinesController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
        Assert.DoesNotContain(typeof(LinesController).GetMethods(), method => method.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false).Length > 0);
    }

    [Fact]
    public async Task The_game_sends_a_line_with_its_voice_and_a_screenshot()
    {
        var form = new LineForm { Text = "「おはよう」", Name = "明日香", Voice = "ASU0001", Game = "Sample Game", Audio = File(Sample.Ogg, "audio"), Screenshot = File(Sample.Jpeg, "screenshot") };

        var view = (await Controller().PostLine(form, _none)).Value!;

        Assert.Equal((1, "「おはよう」", "明日香", "Sample Game", true, true), (view.Id, view.Text, view.Name, view.Game, view.HasAudio, view.HasPicture));
        var kept = _test.Lines.Find(1)!;
        Assert.Equal("ASU0001", kept.Voice);
        Assert.Equal(Sample.Ogg, System.IO.File.ReadAllBytes(_test.Lines.AudioPath(kept)!));
    }

    [Fact]
    public async Task A_line_without_text_or_with_a_file_that_is_no_sound_or_picture_is_refused()
    {
        Problem((await Controller().PostLine(new LineForm { Text = " " }, _none)).Result, StatusCodes.Status400BadRequest);
        Problem((await Controller().PostLine(new LineForm { Text = "a", Audio = File(Sample.Jpeg, "audio") }, _none)).Result, StatusCodes.Status400BadRequest);
        Problem((await Controller().PostLine(new LineForm { Text = "a", Screenshot = File("<html>"u8.ToArray(), "screenshot") }, _none)).Result, StatusCodes.Status400BadRequest);
        var large = new FormFile(Stream.Null, 0, LinesController.MaxFileBytes + 1, "audio", "audio");
        Problem((await Controller().PostLine(new LineForm { Text = "a", Audio = large }, _none)).Result, StatusCodes.Status413PayloadTooLarge);

        Assert.Equal(0, _test.Lines.Count());
    }

    [Fact]
    public async Task The_page_reads_the_newest_lines_and_asks_for_more_by_number()
    {
        for (var number = 1; number <= 5; number++)
        {
            Add("line " + number, voice: number % 2 == 0, picture: false);
        }

        var newest = (await Controller().GetLines(limit: 2)).Value!;
        var after = (await Controller().GetLines(after: 3)).Value!;
        var before = (await Controller().GetLines(before: 4, limit: 2)).Value!;

        Assert.Equal([4, 5], newest.Lines.Select(line => line.Id));
        Assert.Equal((5, 1000), (newest.Total, newest.MaxLines));
        Assert.Equal([true, false], newest.Lines.Select(line => line.HasAudio));
        Assert.Equal([4, 5], after.Lines.Select(line => line.Id));
        Assert.Equal([2, 3], before.Lines.Select(line => line.Id));
    }

    [Fact]
    public async Task Emptying_the_stream_removes_every_line()
    {
        Add("one");

        Assert.IsType<NoContentResult>(await Controller().DeleteLines());

        Assert.Equal(0, _test.Lines.Count());
    }

    [Fact]
    public async Task Without_the_key_nothing_is_read_or_changed()
    {
        Add("one");
        _test.SignOut();

        Problem((await Controller().GetLines()).Result, StatusCodes.Status403Forbidden);
        Problem((await Controller().PostLine(new LineForm { Text = "a" }, _none)).Result, StatusCodes.Status403Forbidden);
        Problem(await Controller().DeleteLines(), StatusCodes.Status403Forbidden);
        Problem(await Controller().GetScreenshot(1), StatusCodes.Status403Forbidden);
        Problem(await Controller().GetPicture(1, cancellationToken: _none), StatusCodes.Status403Forbidden);
        Problem(await Controller().GetAudio("1", cancellationToken: _none), StatusCodes.Status403Forbidden);
        Problem((await Controller().PostPictureToAnki(1, cancellationToken: _none)).Result, StatusCodes.Status403Forbidden);
        Problem((await Controller().PostAudioToAnki("1", cancellationToken: _none)).Result, StatusCodes.Status403Forbidden);
        var controller = Controller();
        await controller.GetEvents(null, _none);
        Assert.Equal(StatusCodes.Status403Forbidden, controller.Response.StatusCode);
        Assert.Equal(1, _test.Lines.Count());
        Assert.Empty(_test.Ffmpeg.Runs);
    }

    [Fact]
    public async Task A_lines_screenshot_is_served_as_the_game_sent_it()
    {
        var line = Add("one");
        var controller = Controller();

        var file = Assert.IsType<PhysicalFileResult>(await controller.GetScreenshot(1));

        Assert.Equal((_test.Lines.PicturePath(line), "image/jpeg"), (file.FileName, file.ContentType));
        Assert.Contains("immutable", controller.Response.Headers.CacheControl.ToString(), StringComparison.Ordinal);
        Add("two", picture: false);
        Problem(await Controller().GetScreenshot(2), StatusCodes.Status404NotFound);
        Problem(await Controller().GetScreenshot(99), StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task A_cards_picture_is_made_from_the_lines_screenshot()
    {
        var line = Add("one");
        var controller = Controller();

        var file = Assert.IsType<FileStreamResult>(await controller.GetPicture(1, "webp", 640, 70, _none));
        await file.FileStream.DisposeAsync();

        Assert.Equal("image/webp", file.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        var call = string.Join(' ', Assert.Single(_test.Ffmpeg.Runs));
        Assert.Contains("-i " + _test.Lines.PicturePath(line), call, StringComparison.Ordinal);
        Assert.Contains("min(640,iw)", call, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_test.Scratch, "work")));
    }

    [Fact]
    public async Task A_picture_of_a_line_that_is_gone_or_has_none_or_in_no_format_is_refused()
    {
        Add("one", picture: false);

        Problem(await Controller().GetPicture(1, cancellationToken: _none), StatusCodes.Status404NotFound);
        Problem(await Controller().GetPicture(7, cancellationToken: _none), StatusCodes.Status404NotFound);
        Add("two");
        Problem(await Controller().GetPicture(2, "gif", cancellationToken: _none), StatusCodes.Status422UnprocessableEntity);
        _test.Ffmpeg.OnRun = (_, _) => Task.FromResult(false);
        Problem(await Controller().GetPicture(2, cancellationToken: _none), StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task A_cards_sound_holds_the_voices_of_the_chosen_lines_in_the_streams_order()
    {
        var one = Add("one");
        Add("narration", voice: false);
        var three = Add("three");

        var file = Assert.IsType<FileStreamResult>(await Controller().GetAudio(" 3, 1,2,3 ", "mp3", 64, true, _none));
        await file.FileStream.DisposeAsync();

        Assert.Equal("audio/mpeg", file.ContentType);
        var call = string.Join(' ', Assert.Single(_test.Ffmpeg.Runs));
        Assert.Contains($"-i {_test.Lines.AudioPath(one)} -i {_test.Lines.AudioPath(three)} -filter_complex", call, StringComparison.Ordinal);
        Assert.Contains("concat=n=2", call, StringComparison.Ordinal);
        Assert.Contains("loudnorm", call, StringComparison.Ordinal);
        Assert.Contains("-b:a 64k", call, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", StatusCodes.Status400BadRequest)]
    [InlineData("one", StatusCodes.Status400BadRequest)]
    [InlineData("1;2", StatusCodes.Status400BadRequest)]
    [InlineData("-1", StatusCodes.Status400BadRequest)]
    [InlineData("2", StatusCodes.Status404NotFound)]
    [InlineData("1,9", StatusCodes.Status404NotFound)]
    public async Task Sound_needs_lines_that_are_there_and_have_a_voice(string lines, int status)
    {
        Add("one");
        Add("narration", voice: false);

        Problem(await Controller().GetAudio(lines, cancellationToken: _none), status);
    }

    [Fact]
    public async Task More_lines_than_one_sound_holds_are_refused()
    {
        var lines = string.Join(',', Enumerable.Range(1, 31));

        Problem(await Controller().GetAudio(lines, cancellationToken: _none), StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task A_picture_and_a_sound_go_into_the_Anki_on_a_server_under_names_of_their_content()
    {
        Add("one");
        Add("two");
        _test.UseServerAnki();
        _test.AnkiNetwork.AnswerWith(request => "\"" + request.Json.GetProperty("params").GetProperty("filename").GetString() + "\"");

        var picture = (await Controller().PostPictureToAnki(2, "webp", cancellationToken: _none)).Value!;
        var sound = (await Controller().PostAudioToAnki("1,2", cancellationToken: _none)).Value!;

        Assert.Matches("^vn-miner_2_[0-9a-f]{10}\\.webp$", picture.FileName);
        Assert.Matches("^vn-miner_1_[0-9a-f]{10}\\.mp3$", sound.FileName);
        Assert.Equal(LinesController.MediaName(2, [1, 2, 3]) + ".webp", picture.FileName);
        Assert.Equal(["storeMediaFile", "storeMediaFile"], _test.AnkiNetwork.Requests.Select(request => request.Action));
        Assert.Equal(new byte[] { 1, 2, 3 }, _test.AnkiNetwork.Requests[0].Json.GetProperty("params").GetProperty("data").GetBytesFromBase64());
        Assert.Equal("server-key", _test.AnkiNetwork.Requests[0].Json.GetProperty("key").GetString());
        Assert.Empty(Directory.GetFiles(Path.Combine(_test.Scratch, "work")));
    }

    [Fact]
    public async Task Nothing_is_made_for_Anki_when_it_could_not_go_anywhere()
    {
        Add("one");

        Problem((await Controller().PostPictureToAnki(1, cancellationToken: _none)).Result, StatusCodes.Status409Conflict);
        Problem((await Controller().PostAudioToAnki("1", cancellationToken: _none)).Result, StatusCodes.Status409Conflict);

        Assert.Empty(_test.Ffmpeg.Runs);
        Assert.Empty(_test.AnkiNetwork.Requests);
    }

    [Fact]
    public void The_same_content_gets_the_same_name_and_other_content_another()
    {
        Assert.Equal(LinesController.MediaName(7, [1, 2, 3]), LinesController.MediaName(7, [1, 2, 3]));
        Assert.NotEqual(LinesController.MediaName(7, [1, 2, 3]), LinesController.MediaName(7, [1, 2, 4]));
        Assert.StartsWith("vn-miner_7_", LinesController.MediaName(7, []), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stream_of_news_starts_with_what_the_page_missed_and_goes_on_with_what_comes()
    {
        Add("one");
        Add("two");
        Add("three");
        var pipe = new Pipe();
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(_none);
        var controller = Controller(pipe.Writer.AsStream(), TimeSpan.FromMilliseconds(40));
        var stream = controller.GetEvents(1, gone.Token);
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);

        Assert.Equal("""hello {"Oldest":1,"Total":3,"MaxLines":1000}""", await NextAsync(reader));
        Assert.Equal("two", Text(await NextAsync(reader)));
        Assert.Equal("three", Text(await NextAsync(reader)));

        Add("four\nwith a second row");
        Assert.Equal("four\nwith a second row", Text(await NextAsync(reader)));

        _test.Shared.MaxLines = 50;
        for (var number = 5; number <= 51; number++)
        {
            Add("line " + number, voice: false, picture: false);
        }

        string news;
        do
        {
            news = await NextAsync(reader);
        }
        while (!news.StartsWith("removed", StringComparison.Ordinal));
        Assert.Equal("""removed {"UpTo":1}""", news);

        _test.Lines.Clear();
        Assert.Equal("cleared {}", await NextAsync(reader));

        // Nothing happens for a while: a comment shows the page that the connection lives.
        string? row;
        do
        {
            row = await reader.ReadLineAsync(_none);
        }
        while (row?.Length == 0);
        Assert.Equal(": beat", row);

        await gone.CancelAsync();
        await stream;
        Assert.Equal("text/event-stream", controller.Response.ContentType);
        Assert.Equal(0, _test.Lines.ListenerCount);
    }

    [Fact]
    public async Task Japanese_is_sent_as_it_is_in_the_news()
    {
        Add("世界が平和でありますように。");
        using var body = new MemoryStream();
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(_none);
        gone.CancelAfter(TimeSpan.FromMilliseconds(150));

        await Controller(body).GetEvents(0, gone.Token);

        Assert.Contains("世界が平和でありますように。", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_so_many_pages_follow_the_stream_at_once()
    {
        var listeners = Enumerable.Range(0, LineStore.MaxListeners).Select(_ => _test.Lines.Listen()).ToList();
        var controller = Controller();

        await controller.GetEvents(null, _none);

        Assert.Equal(StatusCodes.Status429TooManyRequests, controller.Response.StatusCode);
        Assert.Equal(LineStore.MaxListeners, _test.Lines.ListenerCount);
        listeners.ForEach(listener => listener.Dispose());
    }

    [Fact]
    public async Task A_page_that_reads_too_slowly_has_its_stream_ended()
    {
        // A page whose connection takes nothing for a while: the first write does not finish.
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 1, resumeWriterThreshold: 1));
        var stream = Controller(pipe.Writer.AsStream()).GetEvents(null, _none);
        for (var number = 0; number <= LineStore.ListenerBacklog + 1; number++)
        {
            Add("line " + number, voice: false, picture: false);
        }

        Assert.Equal(0, _test.Lines.ListenerCount);
        Assert.False(stream.IsCompleted);

        // Once it reads again it gets what waited for it, and then the stream ends.
        var read = pipe.Reader.AsStream();
        var drained = Task.Run(
            async () =>
            {
                var buffer = new byte[16 * 1024];
                while (await read.ReadAsync(buffer, _none) > 0)
                {
                    // Taken and thrown away.
                }
            },
            _none);
        await stream.WaitAsync(TimeSpan.FromSeconds(20), _none);
        await pipe.Writer.CompleteAsync();
        await drained.WaitAsync(TimeSpan.FromSeconds(20), _none);
    }

    [Theory]
    [InlineData("12", new long[] { 12 })]
    [InlineData("3,1,2,3", new long[] { 1, 2, 3 })]
    [InlineData(" 5 , 4 ", new long[] { 4, 5 })]
    public void Numbers_are_read_once_each_in_rising_order(string text, long[] expected)
    {
        Assert.Equal(expected, LinesController.ReadNumbers(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("0")]
    [InlineData("1,x")]
    [InlineData("1.5")]
    [InlineData("+3")]
    public void What_is_no_list_of_numbers_is_none(string? text)
    {
        Assert.Null(LinesController.ReadNumbers(text));
    }

    /// <summary>Reads one event: its name and its data, joined by a space.</summary>
    private async Task<string> NextAsync(StreamReader reader)
    {
        string? name = null;
        while (true)
        {
            var row = await reader.ReadLineAsync(_none) ?? throw new EndOfStreamException();
            if (row.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = row[7..];
            }
            else if (row.StartsWith("data: ", StringComparison.Ordinal) && name is not null)
            {
                return name + " " + row[6..];
            }
        }
    }

    private static string Text(string news)
    {
        Assert.StartsWith("line ", news, StringComparison.Ordinal);
        return JsonDocument.Parse(news[5..]).RootElement.GetProperty("Text").GetString()!;
    }
}
