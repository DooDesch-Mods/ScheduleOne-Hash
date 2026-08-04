# Changelog

All notable changes to hash are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-08-04

### Added

- The console key takes the phone out with a terminal on it. Type as before: `give ogkushseed 5` reaches the game
  exactly as it always did.
- You see what a command said. Output, warnings and errors land in the terminal instead of in a log file behind the
  game, so `give bananas 1` finally says why nothing happened.
- Tab completes commands and their arguments - items, properties, NPCs, vehicles, weather - and every row says
  which mod supplied it. `give fert` finds `long_life_fertilizer`, `give fzr` still finds it.
- `help` lists all 63 commands with the description the game keeps to itself, `help give` explains one, and
  `help weather` searches. Up walks what you ran before, across sessions; `Ctrl+R` searches back through it.
- `alias gk "give ogkush 5"`, `;` between two commands, `repeat 5 give ogkush 1`, `grep`, `copy` to the system
  clipboard, and `logs` for everything the game and other mods are logging. `raw <line>` turns all of that off for
  a command whose arguments contain a `;` or a quote.
  - `HijackConsoleKey` in `MelonPreferences.cfg` brings the vanilla console bar back.
