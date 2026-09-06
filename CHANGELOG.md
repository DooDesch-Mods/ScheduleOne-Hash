# Changelog

All notable changes to hash are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.2.1] - 2026-09-06

### Changed

- A shared request now records which language your game is in, so a request you typed in German is judged
  against the German model rather than the English one. Still no name and no save.

## [1.2.0] - 2026-09-06

### Added

- The first time you use `# `, hash says it keeps your requests in a file and asks. `share on` sends them so the
  model can learn from real ones, `share off` leaves them alone, and it asks once.
- `share` on its own says where it stands. `NeedleShareUsage` in `MelonPreferences.cfg` is the same switch.
- What players send is counted in the open at https://hash.doomods.com - how often hash was right, and which
  commands it gets wrong.

### Fixed

- Building hash from source left the bundled model out, so you got the untuned one: right about one request in
  ten instead of four in five. The log now says which one loaded.

## [1.1.0] - 2026-09-06

### Added

- Type `# give me five OG Kush` and hash runs `give ogkush 5`. It reads the commands your game actually has, and
  it works offline with no account and no key.
- English, German, Spanish and French all work, and so does a request that needs two commands at once.
- A request hash is sure about runs straight away. Between 50% and 79% it shows you the command and waits for a
  bare `#`; below that it says no rather than guessing.
- Every request you type this way is kept in `UserData/Hash/queries.jsonl` so the next model can learn from what players actually
  type. Switch on `NeedleShareUsage` to send it; it stays on your machine until you do.
- `NeedleKeepContext` lets a later request refer to the one before it. Off by default, so each line stands alone.

### Fixed

- Every command and every argument is checked against your game before any of them runs, so a request that maps
  onto something that is not there is refused instead of half-run.

## [1.0.5] - 2026-08-10

### Added

- `@` is how many you are carrying, and `+ - * /` work where a command wants a number. `give #hand 10-@` tops the stack up to ten, `setquantity @*2` doubles it.

### Fixed

- `packageproduct`, `setquality` and `setquantity` act on what you are holding. All three answered "No product equipped" from the terminal, because taking the phone out empties your hand.

## [1.0.4] - 2026-08-09

### Fixed

- Right-click closes the terminal on the first press. It took three: one was swallowed while the prompt held the caret, one cleared the suggestion list, one shut the app.
- Right-click leaves the way you came in. Opened with the console key it puts the phone away; opened from the home screen it goes back there.
- Escape clears the suggestion list, right-click no longer does. An empty prompt lists every command, so that press was being spent on a block nobody opened.
- The caret returns to the prompt after clicking in the transcript.

## [1.0.3] - 2026-08-05

### Changed

- Commands that other mods register through S1API are read through `ConsoleHelper.RegisteredCommands`, which
  S1API 3.1.9 added for exactly this. Nothing changes on screen; what changes is that the listing no longer
  depends on reaching into S1API's internals, where a rename would have made those commands disappear without a
  word. S1API 3.1.8 and older still work through the old route.

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
