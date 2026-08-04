# hash - A Terminal for Schedule I

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/hash](https://support.doodesch.de/hash).

> **Press the console key and get a terminal.** It completes commands and their arguments, it shows what a
> command printed, and it remembers what you typed last session. The vanilla console is one input field with no
> output, no help and no memory.

![Version](https://img.shields.io/badge/version-1.0.1-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![Sideload](https://img.shields.io/badge/Sideload-required-orange)

**[Source](https://github.com/DooDesch-Mods/ScheduleOne-Hash)** · **[Sideload](https://github.com/DooDesch-Mods/ScheduleOne-Sideload)** · **[Support](https://support.doodesch.de/hash)**

## What you get

- **Tab completes.** Command words, item ids, npc ids, properties, vehicles, quests, weather, keys. `give ogk`
  finds OG Kush; `teleport bar` finds the barn.
- **The arguments are written down.** The line above the prompt shows the shape of the command you are typing
  and highlights the argument you are on, so `setrelationship` stops being guesswork.
- **You can see what happened.** Vanilla runs `give bananas 1` and shows you nothing, with the reason sitting in
  a log file behind the game. hash prints the answer.
- **`#` is whatever you are looking at**, so you never look an id up. `setrelationship # 5` maxes out the
  person in front of you, `give #hand 5` is five more of what you are holding, `setowned #home` buys the place
  you are standing in. Also `#car`, `#near`, `#it` and `#last`.
- **It remembers.** History across sessions, aliases you name yourself, and `logs` to watch the game talk.

Everything is a command: `help` lists them, `help <topic>` narrows, `help <command>` explains one.

## Requirements

MelonLoader 0.7.3+ and [Sideload](https://thunderstore.io/c/schedule-i/p/DooDesch/Sideload/) 1.7.0+. The console
has to be switched on in the game's own settings (Settings > Gameplay), and it is host only - that is the game's
rule, not this mod's. hash says so instead of ignoring you.

## For mod authors

If your mod answers the console with a Harmony prefix, its words are invisible to every list and autocomplete in
the game. Compile in `Hash.Api/HashCommands.cs` and declare them:

```csharp
using Hash.Api;

HashCommands.Add("snitch", "profiler: start, stop, top, report", "snitch start");
```

One file, no references, no hard dependency, and it puts the word in the game's own command list - so every tool
sees it, not only hash.
