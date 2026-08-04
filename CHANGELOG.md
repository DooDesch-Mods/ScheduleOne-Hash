# Changelog

All notable changes to hash are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.0.2] - 2026-08-05

### Added

- A quiet mark in the top right of the log window: the hash glyph punched out of the panel, with the terminal's
  own green bleeding around it. Set so the brightest pixel of it stays under the dimmest text on screen, and a
  line of output crossing it reads exactly as it does anywhere else.

## [1.0.1] - 2026-08-04

### Fixed

- `#hand` names the item you are holding. It answered "nothing there right now" every time, because taking the
  phone out deselects the hotbar - the one moment the mark is asked is the one moment the game has no answer.
  What you last held is remembered while the phone is down.

## [1.0.0] - 2026-08-04

### Added

- The console key takes the phone out with a terminal on it. Type as before - `give ogkushseed 5` reaches the game
  exactly as it always did - and now you see what it answered. Vanilla writes that into a log file behind the game
  and shows you nothing, which is why `give bananas 1` looked like it worked.
- Tab completes the command and its arguments: items, NPCs, properties, vehicles, quests, weather, keys. The line
  above the prompt draws the shape of the command you are typing and marks the argument you are on, so
  `setrelationship` stops being guesswork.
- `#` is whatever you are looking at, so you never look an id up. Face someone and `setrelationship # 5` maxes
  them out without knowing they are `benji_coleman`. `give #hand 5` is five more of whatever you are holding,
  `setowned #home` buys the property you are standing in, and `teleport #it` goes to the id that just printed.
  A `#` pointing at nothing refuses the line instead of guessing.
- Every command in one list, filed under the mod that supplied it, with the description the game keeps to itself.
  `help` shows the common six plus a map of the topics, `help <topic>` opens one, `help <command>` explains one.
- History that survives a restart, aliases you name yourself, `;` between two commands, `repeat 5 <command>`,
  `grep`, `copy` to the system clipboard, and `logs` for what the game is saying as it happens.
  - `font` switches between the machine's own monospaced face and the game's pixel one.
  - `HijackConsoleKey` in `MelonPreferences.cfg` brings the vanilla console bar back.
