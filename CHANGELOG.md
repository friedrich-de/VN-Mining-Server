# Changelog

## 0.1.0

The first version.

- The server takes the lines a game shows, each with who speaks, the voice file and a screenshot, at `POST /Api/Lines` with an API key. One stream of lines, which keeps the newest 1000 (50 to 20000, in the settings) and lets the oldest go.
- The page shows the lines as they come and keeps the newest in view, catches up when you come back to it from the game, and reads earlier lines when asked.
- Cards for Anki from one line or several in a row: the sentence, the screenshot of the first line that has one, and the voices of the lines joined into one sound. They are added to the card just made with Yomitan, or make a new card, through an AnkiConnect on the device or on a server.
- Words are looked up in Yomitan dictionaries kept on the server, with conjugations undone, frequencies, pitch accents and, with an audio source, their sound. The popup's + button makes a card of the word.
- One API key for the game and the page. A browser signs in with it once and keeps a cookie; the page's settings hand out the file `mining.txt` that a game reads the server's address and the key from.
- The page installs as an app.
- A container image with ffmpeg inside, and a Compose file.
