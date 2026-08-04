# Changelog

All notable changes to hash are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-08-04

### Added

- The console key takes the phone out with a terminal on it. Type as before - `give ogkushseed 5` reaches the game
  exactly as it always did - and now you see what it answered. Vanilla writes that into a log file behind the game
  and shows you nothing, which is why `give bananas 1` looked like it worked.
- Tab completes the command and its arguments: items, NPCs, properties, vehicles, quests, weather, keys. The line
  above the prompt draws the shape of the command you are typing and marks the argument you are on, so
  `setrelationship` stops being guesswork.
- `#` is whatever you are looking at. Aim at someone and `teleport #` goes to them, `sethealth # 100` heals them.
  `#car`, `#home`, `#hand`, `#near` and `#last` cover the rest, and a `#` pointing at nothing refuses the line
  instead of guessing.
- Every command in one list, filed under the mod that supplied it, with the description the game keeps to itself.
  `help` shows the common six plus a map of the topics, `help <topic>` opens one, `help <command>` explains one.
- History that survives a restart, aliases you name yourself, `;` between two commands, `repeat 5 <command>`,
  `grep`, `copy` to the system clipboard, and `logs` for what the game is saying as it happens.
  - `font` switches between the machine's own monospaced face and the game's pixel one.
  - `HijackConsoleKey` in `MelonPreferences.cfg` brings the vanilla console bar back.
