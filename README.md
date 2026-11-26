# VN Mining Server

Read a visual novel on your phone or computer and make Anki cards from its lines on any device, with the sentence, the voice and a screenshot.

The game sends each line it shows to this server. The server's page shows the lines as they come, looks words up in dictionaries kept on the server, and sends cards to Anki. Nothing but a browser is needed on the device you mine on.

Watching anime on Jellyfin? [Subtitle Miner](https://github.com/friedrich-de/jellyfin-subtitleminer) does the same for subtitles, and this server shares its page and its dictionary.

## What it does

- **Takes the game's lines.** Text, who speaks, the voice file and a screenshot, sent by the game with an API key. There is one stream of lines. It keeps the newest 1000 and lets the oldest go, so it never grows without end. How many it keeps can be changed.
- **Shows them as they come.** Open the page next to the game, or switch to it from the game on the same phone. The newest line is in view, and coming back to the page from the game catches up by itself. Scroll back to read, and one tap takes you to the newest line again.
- **Makes Anki cards.** Pick one line or several in a row, see the screenshot and hear the voices the card will get, then either add them to the card you just made with Yomitan, or make a new card. The voices of several lines are joined into one sound.
- **Looks words up.** Tap a word in a line, or hold Shift over it as with Yomitan, and a popup shows it from Yomitan dictionaries kept on the server, with conjugations undone, frequencies and pitch accents. Its + button makes a card of the word, its reading, meaning, frequency, pitch and sound, with the sentence, the screenshot and the voice of the line. No browser extension is needed, so it works on a phone too.
- **Installs as an app** on a phone, tablet or computer, from the browser's menu.

## Run it

With Docker:

```sh
git clone https://github.com/friedrich-de/VN-Mining-Server.git
cd VN-Mining-Server
docker compose up -d --build
```

The server then answers on this machine at `http://127.0.0.1:8733`. Its data (the lines with their voices and screenshots, the dictionaries, the settings and the API key) is in the volume `vn-mining-data`.

Without Docker you need the .NET 10 SDK, Node.js 22 or newer and ffmpeg:

```sh
(cd web && npm ci && npm run build)
dotnet run --project VnMiningServer -c Release
```

### The API key

One key opens everything: the game sends its lines with it, and the page asks for it once per browser. The first time the server starts it makes a key and keeps it in `api-key.txt` in its data folder:

```sh
docker compose exec vn-mining-server cat /data/api-key.txt
```

To choose the key yourself, set `VN_MINING_API_KEY` (see `compose.yaml`) to something long and random; the server takes none under 16 characters. A browser stays signed in with a cookie that script cannot read. After ten wrong keys in a minute from one address, the server takes no more keys from that address for a minute.

### HTTPS

Out of the box only the machine the server runs on reaches it. To use it from a phone, put a tunnel or a reverse proxy with HTTPS in front of `127.0.0.1:8733`, for example Cloudflare Tunnel, Caddy or Tailscale Serve. HTTPS is what lets the browser install the page as an app, and it keeps the key and your lines private on the way. The server believes the `X-Forwarded-Proto`, `X-Forwarded-For` and `CF-Connecting-IP` headers of a proxy on the same machine or in a private network.

### Settings of the server

| Environment variable | Default | What it is |
| --- | --- | --- |
| `VN_MINING_DATA` | `data` | The folder for everything the server keeps. |
| `VN_MINING_LISTEN` | `http://127.0.0.1:8733` | The address the server listens on. |
| `VN_MINING_API_KEY` | made on first start | The API key. |
| `VN_MINING_FFMPEG` | `ffmpeg` | The ffmpeg to run, when it is not on the PATH. |

## Connecting a game

A game that can send its lines reads a small text file, `mining.txt`, from its own folder:

```text
server = https://vn.example.org
key = the-api-key
```

The page hands the file out with both values filled in: open the settings, and under "The game" press **Download mining.txt**. Put it into the game's folder and start the game. Keep the file to yourself, since it holds the key.

Games that send their lines this way:

| Game | Runs on | |
| --- | --- | --- |
| 俺たちに翼はない | iPhone, iPad, Apple Vision Pro, Mac | [friedrich-de/Oretsuba_iOS](https://github.com/friedrich-de/Oretsuba_iOS), a native port that sends each line you read with its voice and a screenshot. Its [docs/MINING.md](https://github.com/friedrich-de/Oretsuba_iOS/blob/main/docs/MINING.md) says where the file goes on each device. |

### Sending lines from your own tool

Anything that can make an HTTP request can send lines: a game engine, a text hooker, a script.

```sh
curl https://vn.example.org/Api/Lines \
  -H "Authorization: Bearer $KEY" \
  -F text='昨日の話、覚えてる？' \
  -F name='ユキ' \
  -F game='Sample Novel' \
  -F audio=@voice.ogg \
  -F screenshot=@screenshot.jpg
```

| Field | |
| --- | --- |
| `text` | The line. Needed; up to 4000 characters. |
| `name` | Who speaks, if someone does. |
| `game` | The game's title, shown at the top of the page and put on cards. |
| `voice` | The name of the voice file in the game. Kept with the line, not shown. |
| `audio` | The voice as a file: Ogg, MP3, WAV, FLAC or M4A, up to 10 MB. |
| `screenshot` | The screen as a file: JPEG, PNG or WebP, up to 10 MB. |

The answer is the line as the server keeps it, with its number. A line sent again while it is still the newest one is kept once, and gains the voice or the screenshot if it had none, so a game may send the text at once and the screenshot a moment later.

The rest of the API is what the page itself uses, under `/Api/`: `Lines` (read and empty the stream), `Events` (the stream as server-sent events), `Lines/{id}/Picture` and `Lines/Audio` (the media of cards), `Settings`, `Dictionaries`, `WordAudio` and `Anki`.

## Making cards

In the page's settings, choose where your cards go:

- **Anki on this device.** Desktop Anki with [AnkiConnect](https://ankiweb.net/shared/info/2055492159), or AnkiConnect for Android, on the device you open the page on. AnkiConnect has to let the page in: the settings show the line to add to `webCorsOriginList` in AnkiConnect's config.
- **Anki on a server.** An AnkiConnect that this server can reach, with its API key, for example an [anki-connect-server](https://github.com/friedrich-de/anki-connect-server). This server sends the cards, the pictures and the sound there for you, so nothing needs to run on the device you mine on. The key stays on this server, encrypted, and only the calls that read, add and fill in notes are passed on, never ones that delete.

Then choose the deck and the note type. If you use [Lapis](https://github.com/donkuri/lapis) (or Kiku, or a copy of either under another name), it is chosen for you the first time you connect, with the deck its newest card is in and every field filled as Lapis means it. For other note types, what goes into each field is guessed from the field names, and can be changed.

There are two ways to make a card from the lines you picked:

- **Add to my newest card**: make the card with your own Yomitan first, then this adds the sentence, the screenshot and the voice to it, keeping the word Yomitan marked. The page names the card it will change, and only takes a card made in the last 15 minutes (you can change that). Undo puts the card back as it was.
- **New card**: makes a card of the lines in your deck. In the dictionary popup, the + button makes one of the word.

A card gets the screenshot of the first chosen line that has one, at the width and in the format of the settings (JPEG, WebP or AVIF), and the voices of the chosen lines one after the other as MP3 or Opus. Lines without a voice, such as narration, add nothing to the sound. The game's title goes into the field for the source (MiscInfo in Lapis).

To send new cards to AnkiWeb right away, press **Sync Anki now**, the round arrows in the top bar.

## The dictionary

No dictionary comes with the server. Add them in the page's settings: Jitendex and JMnedict with one tap, or any Yomitan dictionary from an address or from files, several at once if you like. The server imports them one after another, each in a few seconds (Jitendex in 2 to 4). Dictionaries can be put in order, turned off and removed. Importing a dictionary that is there already puts the new import in its place. Frequency and pitch accent dictionaries in Yomitan's format work as well.

If a dictionary's small pictures, such as Daijirin's labels 漢 and 文, say "Image failed to load", it was imported by a version before 0.2.1, which kept pictures with Shift-JIS names in the zip under names no definition asks for. Import it once more.

Looking up can be turned off in the settings, for example to keep using your own Yomitan with "Add to my newest card". There, too, the popup's text can be made smaller or larger, its tags hidden, and definitions laid out compactly.

For the sound of words, give the address of an audio source in the same settings, as Yomitan's "Custom URL (JSON)" takes it, with `{term}` and `{reading}` where the word and its reading go. The server fetches the sounds from there, so the address and any key in it stay on the server.

## Installing it as an app

Open the page and choose "Install" or "Add to Home Screen" in the browser's menu. Browsers only offer this over HTTPS (or on the machine the server runs on, at `localhost`). The app keeps only the page's own files, never lines, pictures, sound or settings, and says so when it cannot reach your server.

## Development

The server is C# for .NET 10 in `VnMiningServer/`. The page is Vue and TypeScript in `web/`; its build is embedded in the server, so the page is built first.

You need the .NET 10 SDK, Node.js 22 or newer and ffmpeg.

```sh
# The server: build (warnings are errors) and unit tests
dotnet build -c Release
dotnet test -c Release

# The page: types, lint, formatting and unit tests
cd web && npm ci && npm run check

# End to end: the page in a real browser at desktop and at phone size, against the built server
dev/e2e.sh

# Dictionaries of your own against the importer, outside the server: what each import counted
# and whether every picture its definitions ask for is there
VN_MINING_TEST_DICTIONARIES=<folder of zips> dotnet test --project VnMiningServer.Tests -c Release -- --filter-class "*RealDictionaryTests"
```

`dev/e2e.sh` needs Playwright's Chromium (`cd web && npx playwright install chromium`). Each test starts a server of its own on a free port with an empty data folder, a pretend game sends it lines with voices and screenshots made by ffmpeg, and cards go to a pretend AnkiConnect.

The dictionary check is the tool for a dictionary that does not import or shows no pictures. It fails with what it found, and writes the same to the file `VN_MINING_TEST_DICTIONARIES_REPORT` names. A dictionary's zip is read as Yomitan reads it, because real dictionaries count on it: a file's name that the packer wrote in its own encoding (Shift-JIS on a Japanese Windows) is taken from the "Unicode Path" field beside it (`Dictionary/ZipNames.cs`), and a checksum that does not fit refuses nothing as long as the file unpacks whole, since some dictionaries are packed with wrong ones.

To work on the page against a running server: `cd web && VN_MINING_URL=http://127.0.0.1:8733 npm run dev`.

A release raises `<Version>` in `Directory.Build.props` and gives it a section in [CHANGELOG.md](CHANGELOG.md).

Nothing runs on GitHub by itself. Whoever makes a change runs the checks above before merging.

## Licence

GPL-3.0, see [LICENSE](LICENSE). The server and its page are built from [Subtitle Miner](https://github.com/friedrich-de/jellyfin-subtitleminer)'s code, which is GPL-3.0.

The rules the dictionary uses to undo Japanese conjugations are [Yomitan](https://github.com/yomidevs/yomitan)'s, as the [Hoshidicts](https://github.com/bee-san/hoshidicts) Japanese language pack writes them (GPL-3.0-or-later); [Dictionary/Japanese/SOURCES.md](VnMiningServer/Dictionary/Japanese/SOURCES.md) says where each file comes from. No dictionary comes with the server: you download or upload the ones you want, under their own licences.

The page shows words with [dictionary-popup](https://github.com/bee-san/dictionary-popup), [Hachidori](https://github.com/bee-san/hachidori)'s popup (GPL-3.0-or-later, its icons Fluent UI System Icons under MIT). A copy is in [web/src/vendor/dictionary-popup](web/src/vendor/dictionary-popup); its `SOURCES.md` says from where and what changed.
