# hash

A terminal on the in-game phone. Press the console key and instead of the grey bar you get a prompt that completes
commands, shows what they printed, and remembers what you typed last time.

🛟 **Need help or found a bug?** Get support at [support.doodesch.de/hash](https://support.doodesch.de/hash).

## What it does that the console does not

The vanilla console is one input field. It has no output, no help, and no memory: run `give bananas 1` and nothing
visibly happens, because the reason is in a log file behind the game.

- **You see what the command said.** Output, warnings and errors land in the terminal, coloured.
- **Tab completes.** Command words and their arguments - item ids, properties, NPCs, vehicles, weather. Type
  `give fert` and it finds `long_life_fertilizer`; type `give fzr` and it still does.
- **Every row says where it came from.** `brickpress   Litterally v1.1.0` - so a list of a thousand item ids from
  six mods reads as six lists.
- **`help`.** The game registers 63 commands with a description and an example each, and shows them to nobody.
  `help` lists them, `help give` explains one, `help weather` searches.
- **Up walks what you ran before**, across sessions. `Ctrl+R` searches backwards through it.
- **Aliases.** `alias gk "give ogkush 5"`.
- **`;` and `repeat`.** `settime 1200 ; setweather clear`, or `repeat 5 give ogkush 1`.
- **`logs`** shows everything the game and every other mod logs, filtered, without alt-tabbing to a file.
- **`copy`** puts a line on your system clipboard, which is how an item id gets into Discord without being retyped.

Ordinary commands are untouched. `give ogkushseed 5` reaches the game byte for byte, and `raw <line>` turns the
shell off entirely for a command whose arguments contain a `;` or a quote.

## Requirements

- [MelonLoader](https://melonwiki.xyz/) 0.7.3
- [Sideload](https://github.com/DooDesch-Mods/ScheduleOne-Sideload) 1.5.0 or newer

Sideload is what draws the app. hash has no home-screen icon on purpose - the console key is the way in - so on an
older Sideload it refuses to start rather than leaving you with a mod you cannot reach.

## Settings

`UserData/MelonPreferences.cfg`, section `Hash`:

- `HijackConsoleKey` (default `true`) - the console key opens hash. Turn it off and the vanilla console bar comes
  back, for a player who prefers it or a mod that needs it.

Your history and aliases live in `UserData/Hash/`. Which commands you use most is remembered per save, beside it.

## Multiplayer

The console is host-only, and the game says so by doing nothing at all when a client presses the key. hash opens
anyway and says why - and `help`, the command reference and the search still work, because looking something up is
not the same as running it.

## Credits

The autocomplete this grew out of started as
[shreyas1996/Schedule_I_AutocompleteConsoleCommand](https://github.com/shreyas1996/Schedule_I_AutocompleteConsoleCommand).
The matching, the ranking and the argument tables here are rewritten, but the idea and the first version came from
that work.

## License

MIT.
