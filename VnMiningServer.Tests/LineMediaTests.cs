using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using VnMiningServer.Media;
using VnMiningServer.Tests.Support;

namespace VnMiningServer.Tests;

public sealed class LineMediaTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vn-mining-media-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFfmpeg _ffmpeg = new();
    private readonly LineMedia _media;
    private readonly CancellationToken _none = TestContext.Current.CancellationToken;

    public LineMediaTests()
    {
        _media = new LineMedia(_ffmpeg, _folder, NullLogger<LineMedia>.Instance);
    }

    public void Dispose()
    {
        _media.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static string Joined(System.Collections.Generic.IReadOnlyList<string> arguments) => string.Join(' ', arguments);

    [Theory]
    [InlineData("jpeg", "jpg", "image/jpeg", "-c:v mjpeg")]
    [InlineData("JPG", "jpg", "image/jpeg", "-c:v mjpeg")]
    [InlineData("webp", "webp", "image/webp", "-c:v libwebp -quality 80")]
    [InlineData("avif", "avif", "image/avif", "-c:v libsvtav1")]
    public async Task A_picture_is_made_in_the_format_asked_for(string format, string extension, string contentType, string codec)
    {
        var result = await _media.PictureAsync("/stream/media/7.jpg", new PictureRequest(format, 640, 80), _none);

        Assert.Equal((CutStatus.Done, contentType, extension), (result.Status, result.ContentType, result.Extension));
        Assert.True(File.Exists(result.Path));
        Assert.EndsWith("." + extension, result.Path, StringComparison.Ordinal);
        var call = Joined(Assert.Single(_ffmpeg.Runs));
        Assert.Contains("-i /stream/media/7.jpg", call, StringComparison.Ordinal);
        Assert.Contains("-frames:v 1", call, StringComparison.Ordinal);
        Assert.Contains("scale='min(640,iw)':-2", call, StringComparison.Ordinal);
        Assert.Contains(codec, call, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_in_an_unknown_format_or_one_ffmpeg_cannot_make_is_refused_before_ffmpeg_runs()
    {
        Assert.Equal(CutStatus.Refused, (await _media.PictureAsync("/a.jpg", new PictureRequest("gif", 640, 80), _none)).Status);

        _ffmpeg.Encoders = " V....D libwebp              WebP \n";
        var result = await _media.PictureAsync("/a.jpg", new PictureRequest("avif", 640, 80), _none);

        Assert.Equal(CutStatus.Refused, result.Status);
        Assert.Contains("avif", result.Message, StringComparison.Ordinal);
        Assert.Empty(_ffmpeg.Runs);
    }

    [Fact]
    public void A_pictures_size_and_quality_stay_within_what_makes_sense()
    {
        var call = Joined(LineMedia.PictureArguments("/a.jpg", "/out.jpg", new PictureRequest("jpeg", 100_000, 500), null));

        Assert.Contains("scale='min(3840,iw)':-2", call, StringComparison.Ordinal);
        Assert.Contains("-q:v 2 ", call, StringComparison.Ordinal);
        Assert.EndsWith(" /out.jpg", call, StringComparison.Ordinal);
    }

    [Fact]
    public void One_voice_becomes_one_sound()
    {
        var call = Joined(LineMedia.AudioArguments(["/media/7.ogg"], "/out.mp3", new AudioRequest("mp3", 96, false)));

        Assert.Contains("-i /media/7.ogg", call, StringComparison.Ordinal);
        Assert.Contains("[0:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a0];[a0]concat=n=1:v=0:a=1[out]", call, StringComparison.Ordinal);
        Assert.DoesNotContain("apad", call, StringComparison.Ordinal);
        Assert.DoesNotContain("loudnorm", call, StringComparison.Ordinal);
        Assert.Contains("-c:a libmp3lame -b:a 96k", call, StringComparison.Ordinal);
        Assert.EndsWith("-f mp3 /out.mp3", call, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_voices_are_joined_in_their_order_with_a_pause_between_two()
    {
        var call = Joined(LineMedia.AudioArguments(["/media/7.ogg", "/media/8.ogg", "/media/10.ogg"], "/out.ogg", new AudioRequest("opus", 48, true)));

        Assert.Contains("-i /media/7.ogg -i /media/8.ogg -i /media/10.ogg", call, StringComparison.Ordinal);
        Assert.Contains("stereo,apad=pad_dur=0.25[a0]", call, StringComparison.Ordinal);
        Assert.Contains("stereo,apad=pad_dur=0.25[a1]", call, StringComparison.Ordinal);
        Assert.Contains("stereo[a2]", call, StringComparison.Ordinal);
        Assert.Contains("[a0][a1][a2]concat=n=3:v=0:a=1,loudnorm=I=-16:TP=-1.5:LRA=11[out]", call, StringComparison.Ordinal);
        Assert.Contains("-c:a libopus -b:a 48k -ar 48000 -f ogg", call, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sound_is_made_as_mp3_or_opus_from_at_least_one_voice()
    {
        Assert.Equal(CutStatus.Refused, (await _media.AudioAsync(["/a.ogg"], new AudioRequest("wav", 96, false), _none)).Status);
        Assert.Equal(CutStatus.Refused, (await _media.AudioAsync([], new AudioRequest("mp3", 96, false), _none)).Status);
        var many = Enumerable.Range(0, LineMedia.MaxAudioLines + 1).Select(number => $"/{number}.ogg").ToList();
        Assert.Equal(CutStatus.Refused, (await _media.AudioAsync(many, new AudioRequest("mp3", 96, false), _none)).Status);
        Assert.Empty(_ffmpeg.Runs);

        var opus = await _media.AudioAsync(["/a.ogg"], new AudioRequest("opus", 48, false), _none);
        Assert.Equal((CutStatus.Done, "audio/ogg", "ogg"), (opus.Status, opus.ContentType, opus.Extension));
    }

    [Fact]
    public async Task What_ffmpeg_fails_at_leaves_no_file()
    {
        _ffmpeg.OnRun = (arguments, _) =>
        {
            File.WriteAllBytes(arguments[^1], [1]);
            return Task.FromResult(false);
        };

        var result = await _media.PictureAsync("/a.jpg", new PictureRequest("jpeg", 640, 80), _none);

        Assert.Equal(CutStatus.Failed, result.Status);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Two_are_made_at_a_time_and_more_than_a_few_waiting_are_turned_away()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var most = 0;
        _ffmpeg.OnRun = async (arguments, _) =>
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await release.Task;
            Interlocked.Decrement(ref running);
            File.WriteAllBytes(arguments[^1], [1, 2, 3]);
            return true;
        };

        var waiting = Enumerable.Range(0, LineMedia.MaxWaiting).Select(_ => _media.PictureAsync("/a.jpg", new PictureRequest("jpeg", 640, 80), _none)).ToList();
        var turnedAway = await _media.PictureAsync("/a.jpg", new PictureRequest("jpeg", 640, 80), _none);
        release.SetResult(true);
        var results = await Task.WhenAll(waiting);

        Assert.Equal(CutStatus.Busy, turnedAway.Status);
        Assert.All(results, result => Assert.Equal(CutStatus.Done, result.Status));
        Assert.Equal(LineMedia.Running, most);
    }

    [Fact]
    public async Task What_an_earlier_run_left_behind_is_swept_away()
    {
        var left = await _media.PictureAsync("/a.jpg", new PictureRequest("jpeg", 640, 80), _none);
        Assert.True(File.Exists(left.Path));

        _media.Sweep();

        Assert.False(File.Exists(left.Path));
    }
}
