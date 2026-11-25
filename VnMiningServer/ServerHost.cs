using System;
using System.IO;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VnMiningServer.Access;
using VnMiningServer.Anki;
using VnMiningServer.Api;
using VnMiningServer.Dictionary;
using VnMiningServer.Lines;
using VnMiningServer.Media;
using VnMiningServer.Settings;

namespace VnMiningServer;

/// <summary>
/// Puts the server together.
/// </summary>
internal static class ServerHost
{
    /// <summary>
    /// Builds the server, ready to run.
    /// </summary>
    /// <param name="options">What it is started with.</param>
    /// <param name="time">The clock. Tests bring their own.</param>
    /// <returns>The server.</returns>
    public static WebApplication Build(ServerOptions options, TimeProvider? time = null)
    {
        var clock = time ?? TimeProvider.System;
        var data = Path.GetFullPath(options.DataFolder);
        var scratch = Path.Combine(data, "scratch");
        var key = ApiAccess.ReadOrMakeKey(options.ApiKey, data, out var keyMade);

        // An empty builder: no settings files to watch, and nothing listens or logs that is not named here.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            // Where the controllers are looked for, also when a test starts the server.
            ApplicationName = typeof(ServerHost).Assembly.GetName().Name,
        });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);
        builder.WebHost.UseUrls(options.Listen);
        if (!options.Quiet)
        {
            builder.Logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            });
        }

        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

        // ASP.NET Core's own key ring comes with its sign-in services and warns about where it is kept.
        // The server does not use it: its sessions are signed with the API key.
        builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

        // Every request without the key would write a line.
        builder.Logging.AddFilter(typeof(KeyAuthenticationHandler).FullName, LogLevel.Warning);

        var services = builder.Services;
        services.AddSingleton(clock);
        services.AddSingleton(new ApiAccess(key, clock));
        services.AddSingleton(new FailureLimiter(clock));
        services.AddSingleton<ICallerResolver, OwnerResolver>();
        services.AddSingleton<IFfmpeg>(provider => new SystemFfmpeg(options.Ffmpeg, provider.GetRequiredService<ILogger<SystemFfmpeg>>()));
        services.AddSingleton<ISharedSettings>(provider => new FileSharedSettings(data, provider.GetRequiredService<ILogger<FileSharedSettings>>()));
        services.AddSingleton(provider => new LineStore(
            Path.Combine(data, "stream"),
            clock,
            () => provider.GetRequiredService<ISharedSettings>().MaxLines,
            provider.GetRequiredService<ILogger<LineStore>>()));
        services.AddSingleton(provider => new LineMedia(
            provider.GetRequiredService<IFfmpeg>(),
            Path.Combine(scratch, "media"),
            provider.GetRequiredService<ILogger<LineMedia>>()));
        services.AddSingleton(provider => new UserSettingsStore(
            Path.Combine(data, "users"),
            new KeyProtector(Path.Combine(data, KeyProtector.FileName)),
            provider.GetRequiredService<ILogger<UserSettingsStore>>()));
        services.AddSingleton(provider => new AnkiClient(AnkiClient.CreateHandler(), provider.GetRequiredService<ILogger<AnkiClient>>()));
        services.AddSingleton(provider => new AnkiGate(provider.GetRequiredService<UserSettingsStore>()));
        services.AddSingleton(provider => new DictionaryServices(
            new DictionaryLibrary(Path.Combine(data, "dictionaries"), provider.GetRequiredService<ILogger<DictionaryLibrary>>()),
            new DictionaryDownloader(DictionaryDownloader.CreateHandler()),
            Path.Combine(scratch, "dictionaries"),
            new WordAudioSource(
                WordAudioSource.CreateHandler(),
                provider.GetRequiredService<ISharedSettings>(),
                provider.GetRequiredService<UserSettingsStore>().Keys,
                provider.GetRequiredService<ILogger<WordAudioSource>>())));
        services.AddSingleton(provider => new AppServices(
            new PageFiles(new EmbeddedPageFiles(typeof(ServerHost).Assembly)),
            provider.GetRequiredService<ICallerResolver>(),
            provider.GetRequiredService<ApiAccess>(),
            provider.GetRequiredService<FailureLimiter>(),
            provider.GetRequiredService<LineStore>(),
            provider.GetRequiredService<LineMedia>(),
            provider.GetRequiredService<UserSettingsStore>(),
            provider.GetRequiredService<ISharedSettings>(),
            provider.GetRequiredService<AnkiClient>(),
            provider.GetRequiredService<AnkiGate>(),
            provider.GetRequiredService<DictionaryServices>(),
            clock));

        services.AddAuthentication(KeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, KeyAuthenticationHandler>(KeyAuthenticationHandler.SchemeName, null);
        services.AddAuthorization();
        services.AddControllers().AddJsonOptions(json =>
        {
            // The names as the code writes them, as the page expects them.
            json.JsonSerializerOptions.PropertyNamingPolicy = null;
            json.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VnMiningServer");
        logger.LogInformation("VN Mining Server {Version}, data in {Data}", AppServices.Version, data);
        if (keyMade)
        {
            logger.LogInformation("A new API key was made. Read it from {Path} on this machine.", Path.Combine(data, ApiAccess.KeyFileName));
        }

        app.Services.GetRequiredService<LineMedia>().Sweep();
        return app;
    }
}
