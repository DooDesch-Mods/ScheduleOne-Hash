# hash

A terminal on the in-game phone. Press the console key and instead of the grey bar you get a prompt that completes
commands, shows what they printed, remembers what you typed last time, and turns a request beginning with `# ` into
real console commands entirely offline.

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/hash](https://support.doodesch.de/hash).

📖 **Documentation:** [docs.doodesch.de/mods/hash/](https://docs.doodesch.de/mods/hash/)

![Version](https://img.shields.io/badge/version-1.0.3-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![Sideload](https://img.shields.io/badge/Sideload-1.7.0+-orange)
![Status](https://img.shields.io/badge/status-working-brightgreen)

**[Sideload](https://github.com/DooDesch-Mods/ScheduleOne-Sideload)** · **[Support](https://support.doodesch.de/hash)**

<img src=".github/media/dan.gif" alt="Dan waiting on the pier at sunset" width="360">

## What it does that the console does not

The vanilla console is one input field. It has no output, no help, and no memory: run `give bananas 1` and nothing
visibly happens, because the reason is in a log file behind the game.

- **You see what the command said.** Output, warnings and errors land in the terminal, coloured.
- **Tab completes.** Command words and their arguments - item ids, properties, NPCs, vehicles, weather. Type
  `give fert` and it finds `long_life_fertilizer`; type `give fzr` and it still does.
- **Every row says where it came from.** `brickpress   Litterally v1.1.0` - so a list of a thousand item ids from
  six mods reads as six lists.
- **`help`.** The game registers every command with a description and an example each, and shows them to nobody.
  `help` lists the common ones plus a map of the topics, `help give` explains one, `help weather` searches.
- **Natural language with `# `.** Type `# give me five OG Kush seeds` and the bundled
  [Cactus Needle](https://github.com/cactus-compute/needle) engine maps the request to the commands currently
  registered in your game. It runs locally, needs no account or API key, and makes no network request during
  inference.
- **`#` is whatever you are looking at**, so you never look an id up. Face someone and `setrelationship # 5`
  maxes them out without knowing they are `benji_coleman`; `setunlocked #` unlocks them. `give #hand 5` is five
  more of whatever you are holding, `setowned #home` buys the property you are standing in, and `teleport #it`
  goes to the id in the line that just printed. A `#` that points at nothing refuses the line instead of
  guessing.
- **`@` is how many you have, and numbers do arithmetic.** `give #hand @` doubles the stack, `give #hand 10-@`
  tops it up to ten, `setquantity @*2` doubles what is in your hand. `+ - * /` and brackets work anywhere a
  command wants a number; anything the game could already read as a number is passed through untouched.
- **Up walks what you ran before**, across sessions. `Ctrl+R` searches backwards through it.
- **Aliases.** `alias gk "give ogkush 5"`.
- **`;` and `repeat`.** `settime 1200 ; setweather clear`, or `repeat 5 give ogkush 1`.
- **`logs`** shows everything the game and every other mod logs, filtered, without alt-tabbing to a file.
- **`copy`** puts a line on your system clipboard, which is how an item id gets into Discord without being retyped.
- **`font`** switches between the machine's own monospaced face and the game's pixel one.

Ordinary commands are untouched. `give ogkushseed 5` reaches the game byte for byte, and `raw <line>` turns the
shell off entirely for a command whose arguments contain a `;` or a quote.

## Natural-language commands

Put `# ` at the beginning of the line, followed by what you want:

```text
# set the time to noon and make it rain
```

Needle returns structured calls, not arbitrary command text. hash builds its tool list from the live console
catalogue, resolves each argument against the same values used by autocomplete, and validates the entire batch
before any command runs.

- At 80% confidence or above, the proposed command or commands run automatically.
- From 50% through 79%, hash prints the proposal. Submit a bare `#` to confirm it.
- Below 50%, an off-topic request, an unknown command, or any invalid argument is refused.
- `Ctrl+C`, closing the terminal, or beginning another line cancels and invalidates the pending request.

The prefix only has this meaning at the start of a line and when followed by text. Marks keep their existing
meaning inside commands: `give # 1`, `give #hand 5`, and the rest behave exactly as before.

## Requirements

- [MelonLoader](https://melonwiki.xyz/) 0.7.3
- [Sideload](https://github.com/DooDesch-Mods/ScheduleOne-Sideload) 1.7.0 or newer

Install the complete release package. `Hash.Needle.bin` must remain beside `Hash.dll` in the `Mods` directory;
the first is the pinned Windows x64 inference engine used by the second.

For a source build, `pwsh Tools/fetch-needle.ps1` downloads the same pinned wheel, verifies both SHA-256 hashes,
and puts the native engine in `Native/`; the Windows post-build step copies it beside the mod automatically.

Sideload is what draws the app, and on a host too old to take the phone out hash refuses to start rather than
leaving you with a mod you cannot reach. The console key is the way in; a home-screen icon appears alongside it
while the game's console is switched on, and goes away when it is not.

## Settings

`UserData/MelonPreferences.cfg`, section `Hash`:

- `HijackConsoleKey` (default `true`) - the console key opens hash. Turn it off and the vanilla console bar comes
  back, for a player who prefers it or a mod that needs it.
- `NeedleKeepContext` (default `false`) - let a later `# ` request refer to earlier Needle requests and their
  command results. The default resets context after every request so each line stands alone.
- `NeedleShareUsage` (default `false`) - upload the `# ` request log so it can improve the model. `share on`
  in the terminal sets the same thing. See below.

Your history and aliases live in `UserData/Hash/`. Which commands you use most is remembered per save, beside it.

## Helping the model get better

hash writes one line per `# ` request to `UserData/Hash/queries.jsonl`: what you typed, the commands it produced,
and whether they worked. The last part is the useful one - a request that ran and was right teaches nothing, a
request you had to type out by hand afterwards teaches exactly what was missing.

**That file never leaves your machine unless you say so.** The first time you use `# `, hash says the file exists
and how to answer: `share on` sends it, `share off` leaves it alone. It asks once. `share` on its own says where
it stands, and `NeedleShareUsage` in `MelonPreferences.cfg` is the same switch for anyone who would rather not
open the terminal to change their mind.

The file is plain text and holds no name, no save and no timestamp, so you can read every line before you decide.
Deleting it is fine at any time; it starts again empty. With sharing on, hash uploads it when you close the
terminal and clears it once the server has it. What arrives is counted in the open at
[hash.doomods.com](https://hash.doomods.com).

The model shipped with hash was trained on requests a language model was asked to invent, which is why it
understands "give me five OG Kush" better than whatever you would actually have typed. Real requests are the only
way past that.

## Multiplayer

The console is host-only, and the game says so by doing nothing at all when a client presses the key. hash opens
anyway and says why - and `help`, the command reference and the search still work, because looking something up is
not the same as running it. A client-side `# ` request is refused before inference starts.

## For mod authors

Two things decide whether your commands work in hash, and both are easy to get wrong because the game gives you no
warning either way.

**Write your answer to Unity's log, or the terminal shows nothing.** hash captures output the way the game's own
console does: `ScheduleOne.Console.Log` is three lines around `UnityEngine.Debug.Log`, and hash listens on
`Application.logMessageReceived`. `MelonLogger` does not go there - it writes to the MelonLoader file log and
nowhere else. A command that answers only through `LoggerInstance.Msg` therefore runs, succeeds, and prints a bare
`ok` into a terminal that never heard it.

```csharp
UnityEngine.Debug.Log("[YourMod] 3 modules, all healthy.");   // reaches hash and the vanilla console
LoggerInstance.Msg("[YourMod] 3 modules, all healthy.");      // reaches the log file only
```

Log both if you want the line in the file too. Keep ordinary diagnostics on `MelonLogger` alone - the game's console
belongs to what the player asked for.

**Declare a prefix command, or nothing can list it.** Handling console input with a Harmony prefix on
`SubmitCommand` is the pattern for anything that wants subcommands or arguments the game's own `ConsoleCommand`
cannot express. It registers nothing: your words run when typed and are invisible to every list, autocomplete and
help overlay, hash included, because they only exist as string literals in a switch.

One call each fixes that:

```csharp
using Hash.Api;

HashCommands.Add("snitch", "profiler: start, stop, top, report", "snitch start");
```

Compile [`Hash.Api/HashCommands.cs`](Hash.Api/HashCommands.cs) into your mod - one file, no reference, no hard
dependency. Every call is a no-op while hash is absent, and calls made before it loads are replayed once it
appears, so load order does not matter. The word goes into the game's own `Console.Commands`, so this reaches the
vanilla list and any other autocomplete as well, not only hash. It is listing only: your prefix keeps running the
command, and the entry never dispatches, so a line cannot run twice.

One trap worth knowing: inside a `MelonMod`, writing `Hash.Api.HashCommands` out in full does not compile.
`MelonBase` has a string property called `Hash`, and the member wins over the namespace. Use `using Hash.Api;`.

## Credits

The autocomplete this grew out of started as
[shreyas1996/Schedule_I_AutocompleteConsoleCommand](https://github.com/shreyas1996/Schedule_I_AutocompleteConsoleCommand).
The matching, the ranking and the argument tables here are rewritten, but the idea and the first version came from
that work.

Natural-language command translation is powered by
[Cactus Compute Needle](https://github.com/cactus-compute/needle), distributed under Apache-2.0. Its license and
third-party notice ship with every release.

## License

MIT.
