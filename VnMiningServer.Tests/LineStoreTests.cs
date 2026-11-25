using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using VnMiningServer.Lines;
using VnMiningServer.Tests.Support;

namespace VnMiningServer.Tests;

public sealed class LineStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vn-mining-stream-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();
    private int _maxLines = LineStore.DefaultMaxLines;
    private LineStore _store;

    public LineStoreTests()
    {
        _store = Open();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private LineStore Open() => new(_folder, _time, () => _maxLines, NullLogger<LineStore>.Instance);

    private StreamLine Add(string text, string? name = null, string? voice = null, byte[]? audio = null, byte[]? picture = null) =>
        _store.Add(new NewLine(text, name, voice, "Sample Game"), audio, picture);

    private string[] MediaFiles() => Directory.Exists(Path.Combine(_folder, "media"))
        ? [.. Directory.GetFiles(Path.Combine(_folder, "media")).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
        : [];

    [Fact]
    public void A_line_is_kept_with_who_says_it_its_voice_and_its_screenshot()
    {
        var line = Add("「おはよう」", "明日香", "ASU0001", Sample.Ogg, Sample.Jpeg);

        Assert.Equal((1, "「おはよう」", "明日香", "ASU0001", "Sample Game"), (line.Id, line.Text, line.Name, line.Voice, line.Game));
        Assert.Equal(_time.Now, line.At);
        Assert.Equal(("ogg", "jpg"), (line.AudioExtension, line.PictureExtension));
        Assert.Equal(Sample.Ogg, File.ReadAllBytes(_store.AudioPath(line)!));
        Assert.Equal(Sample.Jpeg, File.ReadAllBytes(_store.PicturePath(line)!));
        Assert.Equal(line, _store.Find(1));
        Assert.Equal(["1.jpg", "1.ogg"], MediaFiles());
    }

    [Fact]
    public void A_line_of_narration_has_neither_name_nor_voice()
    {
        var line = Add("  空は青かった。\r\n風が吹いた。  ", "  ", string.Empty);

        Assert.Equal("空は青かった。\n風が吹いた。", line.Text);
        Assert.Null(line.Name);
        Assert.Null(line.Voice);
        Assert.Null(_store.AudioPath(line));
        Assert.Null(_store.PicturePath(line));
    }

    [Fact]
    public void A_line_needs_text_and_files_of_a_kind_the_server_knows()
    {
        Assert.Throws<ArgumentException>(() => Add("   "));
        Assert.Throws<ArgumentException>(() => Add("text", audio: [1, 2, 3, 4]));
        Assert.Throws<ArgumentException>(() => Add("text", picture: Sample.Ogg));
        Assert.Equal(0, _store.Count());
        Assert.Empty(MediaFiles());
    }

    [Fact]
    public void The_same_line_sent_twice_in_a_row_is_kept_once()
    {
        var first = Add("「おはよう」", "明日香", "ASU0001", Sample.Ogg, Sample.Jpeg);
        var again = Add("「おはよう」", "明日香", "ASU0001", Sample.Ogg, Sample.Png);

        Assert.Equal(first, again);
        Assert.Equal(1, _store.Count());
        Assert.Equal(Sample.Jpeg, File.ReadAllBytes(_store.PicturePath(again)!));

        // Said again later, after another line, it is a new line.
        Add("「うん」");
        Assert.Equal(3, Add("「おはよう」", "明日香", "ASU0001").Id);
    }

    [Fact]
    public void A_repeat_adds_what_the_first_copy_lacks()
    {
        using var listener = _store.Listen();
        Add("「おはよう」", "明日香", "ASU0001");

        var again = Add("「おはよう」", "明日香", "ASU0001", Sample.Ogg, Sample.Jpeg);

        Assert.Equal((1, "ogg", "jpg"), (again.Id, again.AudioExtension, again.PictureExtension));
        Assert.Equal(again, _store.Find(1));
        Assert.Equal(["1.jpg", "1.ogg"], MediaFiles());
        Assert.True(listener.News.TryRead(out _));
        Assert.True(listener.News.TryRead(out var news));
        Assert.Equal((StreamEventKind.Added, again), (news!.Kind, news.Line));
    }

    [Fact]
    public void Only_the_newest_lines_are_kept_and_the_oldest_go_with_their_files()
    {
        _maxLines = 50;
        using var listener = _store.Listen();
        for (var number = 1; number <= 53; number++)
        {
            Add("line " + number, audio: Sample.Ogg, picture: Sample.Jpeg);
        }

        Assert.Equal(50, _store.Count());
        Assert.Equal(4, _store.Oldest());
        Assert.Null(_store.Find(3));
        Assert.Equal(100, MediaFiles().Length);
        Assert.DoesNotContain("3.ogg", MediaFiles());
        Assert.Contains("4.ogg", MediaFiles());

        var removed = Enumerable.Range(0, 200).Select(_ => listener.News.TryRead(out var news) ? news : null).Where(news => news?.Kind == StreamEventKind.Removed).ToList();
        Assert.Equal([1, 2, 3], removed.Select(news => news!.UpTo));
    }

    [Theory]
    [InlineData(0, LineStore.DefaultMaxLines)]
    [InlineData(-5, LineStore.DefaultMaxLines)]
    [InlineData(1, LineStore.MinMaxLines)]
    [InlineData(500, 500)]
    [InlineData(1_000_000, LineStore.MaxMaxLines)]
    public void The_number_of_lines_to_keep_stays_within_bounds(int asked, int kept)
    {
        Assert.Equal(kept, LineStore.ClampMaxLines(asked));
    }

    [Fact]
    public void A_smaller_number_to_keep_takes_effect_when_asked()
    {
        for (var number = 1; number <= 80; number++)
        {
            Add("line " + number);
        }

        _maxLines = 60;
        _store.Trim();

        Assert.Equal(60, _store.Count());
        Assert.Equal(21, _store.Oldest());
    }

    [Fact]
    public void Lines_are_read_newest_after_a_number_or_before_one_always_oldest_first()
    {
        for (var number = 1; number <= 10; number++)
        {
            Add("line " + number);
        }

        Assert.Equal([8, 9, 10], _store.Latest(3).Select(line => line.Id));
        Assert.Equal([4, 5], _store.After(3, 2).Select(line => line.Id));
        Assert.Empty(_store.After(10, 5));
        Assert.Equal([5, 6, 7], _store.Before(8, 3).Select(line => line.Id));
        Assert.Empty(_store.Before(1, 3));
        Assert.Single(_store.Latest(0));
    }

    [Fact]
    public void An_emptied_stream_keeps_no_file_and_gives_no_number_twice()
    {
        using var listener = _store.Listen();
        Add("one", audio: Sample.Ogg, picture: Sample.Jpeg);
        Add("two");

        _store.Clear();

        Assert.Equal(0, _store.Count());
        Assert.Equal(0, _store.Oldest());
        Assert.Empty(MediaFiles());
        Assert.Equal(3, Add("three").Id);
        var kinds = Enumerable.Range(0, 10).Select(_ => listener.News.TryRead(out var news) ? news!.Kind : (StreamEventKind?)null).Where(kind => kind is not null).ToList();
        Assert.Equal([StreamEventKind.Added, StreamEventKind.Added, StreamEventKind.Cleared, StreamEventKind.Added], kinds);
    }

    [Fact]
    public void The_stream_survives_a_restart()
    {
        var line = Add("「おはよう」", "明日香", "ASU0001", Sample.Ogg, Sample.Jpeg);
        _store.Dispose();
        _store = Open();

        Assert.Equal(line, _store.Find(1));
        Assert.Equal(Sample.Ogg, File.ReadAllBytes(_store.AudioPath(line)!));
        Assert.Equal(2, Add("next").Id);
    }

    [Fact]
    public void A_very_long_line_is_cut_and_not_in_the_middle_of_a_character()
    {
        var text = new string('あ', LineStore.MaxTextLength - 1) + "𠮷野家";

        var line = Add(text, new string('n', 500));

        Assert.Equal(LineStore.MaxTextLength - 1, line.Text.Length);
        Assert.False(char.IsHighSurrogate(line.Text[^1]));
        Assert.Equal(LineStore.MaxNameLength, line.Name!.Length);
    }

    [Fact]
    public void Every_page_that_follows_gets_the_news_until_it_stops_following()
    {
        var one = _store.Listen();
        using var two = _store.Listen();
        Assert.Equal(2, _store.ListenerCount);

        Add("first");
        one.Dispose();
        Add("second");

        Assert.Equal(1, _store.ListenerCount);
        Assert.True(one.News.TryRead(out var news));
        Assert.Equal("first", news!.Line!.Text);
        Assert.False(one.News.TryRead(out _));
        Assert.True(one.News.Completion.IsCompleted);
        Assert.Equal(2, Enumerable.Range(0, 5).Count(attempt => two.News.TryRead(out _)));
    }

    [Fact]
    public void A_page_that_reads_too_slowly_is_cut_off_to_catch_up_anew()
    {
        using var slow = _store.Listen();
        for (var number = 0; number <= LineStore.ListenerBacklog; number++)
        {
            Add("line " + number);
        }

        Assert.Equal(0, _store.ListenerCount);
        Assert.Equal(LineStore.ListenerBacklog, Enumerable.Range(0, LineStore.ListenerBacklog + 10).Count(attempt => slow.News.TryRead(out _)));
        Assert.True(slow.News.Completion.IsCompleted);
    }

    [Fact]
    public void A_store_that_is_closed_ends_its_listeners()
    {
        using var listener = _store.Listen();

        _store.Dispose();

        Assert.True(listener.News.Completion.IsCompleted);
        Assert.True(_store.Listen().News.Completion.IsCompleted);
    }

    [Theory]
    [InlineData("ogg", true)]
    [InlineData("jpg", false)]
    public void The_kind_of_a_file_is_told_from_its_first_bytes(string expected, bool audio)
    {
        Assert.Equal(expected, audio ? MediaKind.Audio(Sample.Ogg) : MediaKind.Picture(Sample.Jpeg));
        Assert.Null(audio ? MediaKind.Audio(Sample.Jpeg) : MediaKind.Picture(Sample.Ogg));
        Assert.Null(MediaKind.Audio([]));
        Assert.Null(MediaKind.Picture([]));
    }

    [Fact]
    public void The_kinds_of_sound_and_pictures_the_server_knows()
    {
        Assert.Equal("mp3", MediaKind.Audio([.. "ID3"u8, 4, 0, 0]));
        Assert.Equal("mp3", MediaKind.Audio([0xFF, 0xFB, 0x90, 0x00]));
        Assert.Equal("flac", MediaKind.Audio("fLaC\0\0\0\""u8));
        Assert.Equal("wav", MediaKind.Audio("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Equal("m4a", MediaKind.Audio([0, 0, 0, 0x20, .. "ftypM4A "u8]));
        Assert.Equal("png", MediaKind.Picture(Sample.Png));
        Assert.Equal("webp", MediaKind.Picture("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(MediaKind.Picture("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Null(MediaKind.Audio("<html><body>"u8));
        Assert.Equal("audio/ogg", MediaKind.ContentType("ogg"));
        Assert.Equal("image/jpeg", MediaKind.ContentType("jpg"));
        Assert.Equal("application/octet-stream", MediaKind.ContentType("exe"));
    }
}
