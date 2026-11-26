# Changelog

## 0.2.1

- Dictionaries packed on a Japanese Windows show their pictures. Some, such as Daijirin with its label pictures (漢, 文, 派生 and the like), come in a zip that writes the pictures' names in Shift-JIS and puts the same names in UTF-8 into a field beside them. Yomitan reads that field and the server did not: it kept those pictures under unreadable names, and the popup said "Image failed to load" wherever a definition asked for one. The server now takes the names as Yomitan does. **Import such a dictionary once more** to get its pictures: the new import takes the old one's place, in the same spot of the list and on or off as it was.

## 0.2.0

- Dictionaries are imported in seconds. Jitendex, with 437,000 words, took 27 seconds and now takes 2 to 4 on the same machine; JMdict went from 15 seconds to under 2. The server reads a dictionary's files side by side on up to 8 processor cores instead of one after another, keeps each definition as the dictionary wrote it instead of taking it apart and writing it anew, and compresses definitions together in blocks instead of one by one, which was two thirds of the time before.
- Imported dictionaries take less room: Jitendex 94 MB instead of 211 MB, JMdict 70 MB instead of 124 MB. Dictionaries imported by 0.1.0 keep working as they are; import one again to make it smaller.
- Several dictionaries can be added at once. Pick as many zips as you like in the file field: they are sent one after another and imported in that order, each as soon as it is there, and the page says at the end which are ready and why any are not. A dictionary added while another is imported waits its turn instead of being turned away. Stop ends the import that runs and lets go of those that wait.
- A dictionary whose zip is damaged or lies about the size of a file in it is refused with the file's name, and leaves nothing behind.

## 0.1.0

The first version.

- The server takes the lines a game shows, each with who speaks, the voice file and a screenshot, at `POST /Api/Lines` with an API key. One stream of lines, which keeps the newest 1000 (50 to 20000, in the settings) and lets the oldest go.
- The page shows the lines as they come and keeps the newest in view, catches up when you come back to it from the game, and reads earlier lines when asked.
- Cards for Anki from one line or several in a row: the sentence, the screenshot of the first line that has one, and the voices of the lines joined into one sound. They are added to the card just made with Yomitan, or make a new card, through an AnkiConnect on the device or on a server.
- Words are looked up in Yomitan dictionaries kept on the server, with conjugations undone, frequencies, pitch accents and, with an audio source, their sound. The popup's + button makes a card of the word.
- One API key for the game and the page. A browser signs in with it once and keeps a cookie; the page's settings hand out the file `mining.txt` that a game reads the server's address and the key from.
- The page installs as an app.
- A container image with ffmpeg inside, and a Compose file.
