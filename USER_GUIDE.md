# HealthHunter 6.1

A guided Windows tool for finding, editing, freezing and saving health values in **single-player / offline** games.
It combines a memory scanner with an on-screen bar reader: the bar reader watches the health bar's pixels, and the
scanner keeps only the memory values that move the same way. Version 6 adds the **Assistant**: one switch, and it finds
the bars, on-screen numbers and your position by itself while you play.

Version 6.1 expands Assistant number scans to byte, signed/unsigned 16-bit, 32-bit and 64-bit integers alongside
Float and Double. The scan and confirmation counts and timings are recorded in Diagnostics so failed finds are easier
to diagnose from a copied log.

HealthHunter refuses to attach to games that load a known anti-cheat (EasyAntiCheat, BattlEye, Vanguard, EA AC, nProtect, etc.).
It contains no anti-cheat bypass and injects no code: it only uses ReadProcessMemory / WriteProcessMemory.

## What's new in 6.0: the Assistant

The **Assistant** page (first in the sidebar) has one switch. Turn it on, play the game, and it does the rest:

* **Attaches to the game in front.** Whatever game window you play in gets attached (after the same anti-cheat check as
  before; refused games stay refused). Explorer, browsers, Steam, Discord and the like are never taken for a game.
* **Finds every HUD bar by itself.** It watches the game window, spots thin rectangles of one colour whose length changes
  with one end fixed (that's a bar), and names them by colour: red → Health, blue → Mana, yellow/green → Stamina/Energy,
  purple → Mana/Energy, and so on. Bars it has seen are remembered in the game's profile.
* **One snapshot, all bars at once.** It takes one snapshot of the game's memory and runs auto-find for every bar from
  it at the same time (up to three), with the same write-once copy test as 5.1, then looks for pointer paths. Found
  values go into the cheat table, get frozen at max (if "Turn cheats on" is ticked) and are saved to the profile.
* **On-screen numbers too** (Windows 10/11 only): ammo, gold, XP, stat-screen numbers. It reads the HUD with the OCR
  built into Windows (nothing is downloaded), watches which numbers change, finds them in memory with exact-value
  scans, confirms each with a single +1 write that the OCR must see on screen, and puts it back. Numbers with a word
  next to them get that name ("Gold", "Ammo"); the rest are "Number at x,y". Stat numbers that never change are looked
  for near values already found (the same character object).
* **Position from walking and jumping.** Stand still, walk, stand still (three times), jump once: it finds the three
  floats that move when you walk and the one that goes up and comes back down on a jump, then pushes the height up a
  little once: the real position falls back under gravity, a render copy is overwritten. Result: a **Position** group
  with "Height" (freeze it = hold height / no fall damage) and the two other axes, with save/restore slots for teleport.
  Hands-off, no key presses are sent; it only checks whether a movement key or the stick is held.
* **Honest about what it can't do.** The Feed and "Found so far" list say exactly what it is waiting for ("make the mana
  bar move", "jump once in place") and why something was given up on. See "What the Assistant can't do" below.
* Everything else (the Auto, Manual, Cheat table, Pointer paths, Memory view and Diagnostics pages, hotkeys,
  controller combos, profiles, groups and slots) is unchanged, and profiles from 4.x/5.x load as before.

### What the Assistant can't do

* **Bars that aren't plain bars.** Segmented or round/arc bars, bars made of icons (hearts), bars that fade or shake all
  the time, and bars with no fixed end aren't detected. Mark those on the Quick start page by hand as before.
* **Numbers the OCR can't read.** Very small, stylised or outlined fonts, numbers over busy backgrounds, and values stored
  differently from how they're shown (a percentage, a rounded or scaled number) aren't found. It needs
  Windows 10 (1809+) or 11; on other systems the Feed says OCR is off and the rest still works.
* **Positions a physics engine owns.** When the position lives inside a physics engine (the game overwrites the float at
  once), is in double precision, or jumping doesn't change a plain float, it says so and gives up. "Turn off gravity" is
  not something that can be found generically: gravity is usually a constant in the engine, not a value next to the
  player, so the Assistant offers **hold height** (freeze Height) and teleport slots instead.
* It can't make enemies miss, give items, or change anything that isn't a number the screen shows. Those need per-game
  work and are out of scope.

## What's new in 5.1

* **Auto-find tells the real health from copies of it.** Many games keep health in one place and copy it elsewhere
  (for the health bar, for example). v5 confirmed a candidate by halving it and watching the bar drop, which a display
  copy passes too, and freezing a copy doesn't keep you alive. 5.1 changes each remaining candidate once and watches
  what the game does with it: a copy goes straight back by itself, the real value keeps the change and its copies
  follow it. Copies are never written a second time.
* **Look-alike values.** When two values both keep a change (usually the real one and a copy the game only refreshes
  when health changes), auto-find raises or lowers one of them a little, asks you for one hit, and checks which one
  the hit started from. It needs a test from above and one from below to agree, so it asks for a few extra hits
  (not heals). The test value is always put back.
* **If it still can't tell**, it adds the candidates to the cheat table as "Health option A / B…" (not frozen) and says
  how to check them: freeze one and take hits that would normally kill you.
* **Safer tests.** Nothing is written while health is low (it asks you to heal first), into a value that just hit 0,
  into temporaries on a thread stack, or into overlapping values (a Float inside a Double). If the bar disappears
  (menu, loading screen, death) the round is dropped; a short flash of the bar on a hit doesn't count as that. If the
  value being tested also changed oddly while the bar was gone, it isn't written again (the game may have reused that
  memory) and the status line tells you, so you can heal or reload. The status line keeps saying what to do next (hit
  or heal) until you do it.
* **Emulators and very big games.** Big scans (the first snapshot, and comparing against it) now pause the game while
  they run, so a hit in the middle can't throw the real value out. Stop, Detach or closing HealthHunter un-pauses the
  game straight away. Known emulators (Kyty, shadPS4, Yuzu-family, Ryujinx, PCSX2, PPSSPP and others) and processes with
  a lot of shared memory also get their shared memory read, where emulators keep the console's RAM. Emulators of
  big-endian consoles (RPCS3, Cemu, Dolphin, Xenia) are refused up front with an explanation, since their numbers are
  stored byte-reversed.
* **Starts over by itself** with a fresh snapshot when it loses track of the value (3 tries in all), instead of giving up.
* **A lone value is checked a bit more.** If only one value is left but changing it didn't move the bar, auto-find
  waits for a couple more changes of the bar before accepting it.
* **Works at any Windows display scaling.** The window, fonts and step cards are measured from the text, so nothing is
  cut off or overlaps at 125-200% scaling or on a second monitor with a different scale.

## What's new in v5

* **New dark interface.** Sidebar navigation, a four-step **Quick start** page (attach → mark the bar → find it → turn it on)
  with done / current / to-do states, a live bar % reading and graph, and cleaner Manual scan, Cheat table, Pointer paths and Memory view pages.
* **No more guessing the value type.** Manual scans default to **Auto (Int32 + Float + Double)** and track all three at once;
  the results list has a Type column. Auto-find tracks all three from a single memory snapshot instead of one type per round.
  (v4 defaulted to Int32 and auto-find spent a full round on Int32 before trying Float, which is how most modern games store health.)
* **Safer memory reads.** v4 read candidates in 64 KB blocks; on Windows one unreadable page inside a block makes the whole read
  fail, which silently dropped the real address for good. v5 retries page by page and keeps everything readable.
* **Float junk filter.** Tiny non-zero floats (an integer's bits read as a float) are rejected, so the same value isn't listed twice.
* **Better bar reading.** The bar % is found by fitting the fill edge instead of counting colored columns, so see-through or
  flashing bars read steadily; bars that drain right-to-left or top-down are detected; a black capture (exclusive fullscreen/HDR) is reported.
* **Auto-find is more forgiving.** Bars that never sit perfectly still (regeneration) are accepted after a few seconds; if
  no value keeps a constant value-to-bar ratio twice in a row, it switches to plain Decreased/Increased instead of giving up.
* **Diagnostics page.** Every scan, bar reading and auto-find decision is logged (also to `%AppData%\HealthHunter\logs`).
  **Run self-check** reports how much game memory exists and can be read, what the bar reader sees, and what table entries read.

---

## Quick start

1. Set the game to **Borderless or Windowed** and turn **HDR off**. Load into gameplay.
2. Open HealthHunter, pick the game at the top, click **Attach**.
3. Step 2 on the Quick start page: with health **full**, click **Select health bar** and drag a box around the bar's colored fill.
   The live reading should say about 100% and drop when you take a hit.
4. Step 3: click **Start auto-find**, go back to the game and take a few hits (healing helps too). It:
   - snapshots memory, then scans every time the bar settles at a new level, tracking Int32, Float and Double together,
   - drops anything whose value doesn't match the bar's % (value ÷ bar% must stay the same = max health),
   - tests the last few candidates by changing each one once and watching whether the game keeps the change
     (the real value) or puts it back (a copy); if two look alike it asks for a few more hits to tell them apart,
   - works out max health, finds pointer paths and saves a profile.
5. Step 4: flip **Infinite health** (or press `Ctrl+Alt+F`). `Ctrl+Alt+R` refills.

**Second launch:** Attach → the switch is ready immediately. If the saved path broke, run auto-find again; it keeps
only the pointer paths that survived the restart, which locks the profile in for good.

**If it isn't found:** open **Diagnostics**, click **Run self-check**, try again, then **Copy log** and send it.

### More than health
Pick **Stamina**, **Mana / FP** or type any name in the "Find" box, then Start Auto-find. Each stat gets its own bar box
and its own switch. Up to six switches show on the Quick start page.

### Freeze modes (per stat, also on the Cheat table page)
| Mode | What it does |
|---|---|
| Lock at max | Infinite: keeps the value at max |
| Never below % | You still take damage, but it can't drop under the floor you set |
| Freeze value | Holds whatever value it had when you switched it on |
| Allow increase | Blocks decreases, allows healing / gains |
| Multiply gains | Gains are multiplied by the "Gain ×" column (x2 gold / XP); losses pass through |

Auto-find also looks for the game's own **max** value stored next to the current one. When found, "max" follows
level-ups automatically (shown as "max at +XX" on the Cheat table page).

The freeze runs on its own thread about 1,000 times a second, which stops combos and damage-over-time. A single hit
larger than your whole bar can still land between two checks. If that happens in a game, use "Never below %" with a
higher floor.

### Cheat Engine tables
The Cheat table page → **Import Cheat Engine table…** loads the plain pointer entries from a `.CT` file (scripts are skipped).
**Export .CT…** writes your found entries so you can open them in Cheat Engine. Groups are kept both ways.

---

## New in v4

**Hotkeys per entry (the Cheat table page).** Double-click the Hotkeys cell (or click **Hotkeys…**). Each entry can have several:
toggle freeze, set to a value, add, subtract, refill, save or restore a slot. Bind a key combo, an Xbox-style controller
button combo, or both. Hotkeys are saved in the profile (for entries that have a pointer path).

**Feedback.** When a hotkey fires you hear a short beep (high = on, low = off) and see a small message in the top-left of
the screen. Turn either off with **Sounds** / **On-screen messages** at the top. The message shows over windowed and
borderless games, not exclusive fullscreen.

**Auto-attach.** Tick **Auto-attach** at the top. When a game you have a profile for is running (for 10 s or more),
HealthHunter attaches by itself, with the same anti-cheat check. If you detach by hand it won't reattach to that same run.
While attached, the anti-cheat check repeats every 5 s and detaches if a protected module appears later.

**Game update warning.** Profiles remember the game exe's size and version. If the game was updated, the status line says
so, which usually explains a broken pointer path.

**More scans (the Manual scan page).** New scan types: Bigger than, Smaller than, Value between (two boxes), Same as first scan. New types:
Byte, UInt16, UInt32. **Undo scan** steps back up to 5 next scans. Scan options: Fast scan (aligned) off checks every
byte for packed values; Include mapped memory; Pause game while scanning (suspends the game during manual scans so values
can't move; it always resumes afterwards); Range limits the first scan to an address range (hex or `game.exe+1000`).

**Groups and slots (the Cheat table page).** Type a name in the Group column (e.g. `Position` for X, Y and Z). Right-click a row to
freeze or unfreeze the whole group, save its values to slot 1–5 or restore them. Saving and restoring position this way
works as a save point / teleport. Slots are stored in the profile. Hotkeys can save and restore slots too.

**Value graph (the Cheat table page).** The chart under the table shows the selected entry over the last minute.

**Memory view (the Memory view page).** Right-click an entry → View in memory, or type an address. Shows the memory around it live
as Int32, Float, Double and bytes, with changed rows in red. Double-click a row to add it to the table. If you came from
an entry with a pointer path, the new entry gets the same path shifted to it, so neighbours like stamina or ammo survive
restarts without another pointer scan.

**Pointer scan options (the Pointer paths page).** "Base in game exe only" keeps paths whose base is the main exe (they tend to survive
updates). "Last offset" keeps only paths whose final offset equals a value you know (e.g. a struct field offset).

---

## Build

1. Install the .NET 8 SDK once: `winget install Microsoft.DotNet.SDK.8`
2. Double-click **build.bat**.
3. Run `publish\HealthHunter.exe`.

v6 targets `net8.0-windows10.0.19041.0` (for the Windows OCR used by the Assistant); it runs on Windows 10 1809 and later.
The build pulls the Windows SDK projection package (`Microsoft.Windows.SDK.NET.Ref`) from NuGet the first time, so the
first build needs internet.

Or open `HealthHunter.csproj` in Visual Studio 2022 and press F5.

If a game runs as administrator (some launchers do), run HealthHunter as administrator too.

---

## Using it

The left panel of the Manual scan page always tells you the next step. In short:

**1. Attach.** Load into gameplay, pick the game in the top list, click Attach. A saved profile loads automatically.

**2. First scan.**
- Number visible: Scan = Exact value, Type = Int32, type the number, First Scan.
- Bar only: Scan = Unknown initial value, Type = Float, First Scan. Or use the Health bar page's Auto-narrow.

**3. Narrow.** Take damage or heal, then Next Scan with the new exact value, or Decreased/Increased. While health is steady, an Unchanged scan removes noise. 3–6 rounds usually gets under 30 results.

**4. Confirm.** Select a result, "Set selected to" 999, and check the game. If health changes and stays changed, it's the real value. If it snaps back, it was a display copy. Add the real one to the table.

**5. Table (the Cheat table page).** Edit the value, set Max, tick Freeze. Global hotkeys:
- `Ctrl+Alt+F`: freeze/unfreeze all entries at Max
- `Ctrl+Alt+R`: refill all to Max
- `Ctrl+Alt+L`: start/stop CSV logging (to `Documents\HealthHunter Logs`)

**6. Make it permanent (the Pointer paths page).** Addresses change every launch. Pointer paths don't.
1. Select the health entry on the Cheat table page, then Scan on the Pointer paths page. Paths are saved automatically.
2. Restart the game, Attach, find health again (steps 2–4), add it to the table and select it.
3. Click "Keep only paths that reach the selected entry". Repeat once more if many remain.
4. Select the top path, "Use selected path for entry", then **Save profile** on the Cheat table page.

From then on, attaching to that game finds health instantly.

Profiles: `%AppData%\HealthHunter\profiles\<game>.json` (also stores the bar calibration).

---

## Troubleshooting

- **0 results:** wrong type. Try Float, then Double. If the shown number is rounded, set ± tolerance to 1.
- **Unknown initial value is slow / uses disk:** it snapshots all writable memory to `%TEMP%\HealthHunter` (deleted after the next scan). Large games can need several GB of free disk.
- **Pointer scan runs out of memory or finds nothing:** lower or raise Max levels (3–5) and Max offset (`1000`–`2000` hex). Unity/Mono games often need 5+ levels.
- **Bar reads black / 0%:** switch the game to borderless windowed. Exclusive fullscreen can't be captured this way.
- **Bar % jumps around:** raise color tolerance, or reselect the region tighter, avoiding text and frame edges.
- **Hotkeys don't fire:** another app owns that combination (the status line names it), or the game runs as admin and HealthHunter doesn't.
- **Controller hotkeys don't fire:** only XInput (Xbox-style) controllers are read. PlayStation pads need Steam Input or DS4Windows.
- **Auto-find added "Health option A, B…" instead of Health:** it found values that behave the same and couldn't tell
  which one the game uses. Freeze one and take hits that would normally kill you; if you survive, keep it, otherwise
  delete it and try the next. Then send the Diagnostics log so the test can be improved.
- **Game stuck after a scan with "Pause game while scanning":** it should always resume; if HealthHunter was killed mid-scan, close the game from Task Manager.

## Files

| File | Purpose |
|---|---|
| `ProcessMemory.cs` | Open process, regions, modules, read/write, anti-cheat check |
| `Scanner.cs` | First/next scans (one or several value types at once), parallel, page-safe reads, snapshot to disk for unknown values |
| `PointerScanner.cs` | Reverse pointer-path search and validation |
| `BarDetector.cs` | Screen capture, color calibration, fill %, region picker |
| `Profiles.cs` | Table entries, JSON profiles, CSV logger |
| `FreezeEngine.cs` | High-rate freeze thread and freeze modes |
| `CheatTable.cs` | Cheat Engine .CT import / export |
| `AutoFinder.cs` | Hands-off finder: bar-driven scans, ratio filter, write-test confirmation, pointer lock-in |
| `MainForm.cs` | Window shell, Quick start, Manual scan, Health bar, Cheat table and Pointer pages, freeze loop, hotkeys |
| `MainForm.V4.cs` | v4: per-entry hotkeys, feedback, auto-attach, groups/slots, graph, memory view, scan options |
| `HotkeyDialog.cs` | Hotkey editor (keys + controller) |
| `Gamepad.cs` | XInput controller polling for controller hotkeys |
| `Overlay.cs` | Click-through on-screen message |
| `ValueGraph.cs` | Live value chart |
| `MainForm.Diag.cs` | v5: Diagnostics page (log + self-check) and Settings page |
| `Diag.cs` | v5: diagnostics log (UI + `%AppData%\HealthHunter\logs`) |
| `Theme.cs` | v5: dark palette, theming of standard controls, dark title bar / scrollbars / menus |
| `UiKit.cs` | v5: rounded buttons, cards, step cards, sidebar navigation, slim progress, status pill |
| `Scaling.cs` | 5.1: display-scaling helpers (DesignScale, ISelfScaling) so the UI fits at any Windows scaling |
| `Assistant.cs` | v6: the hands-off loop: attach to the game in front, watch the window, one shared snapshot, finders for every bar, numbers and position |
| `HudDetector.cs` | v6: finds HUD bars in a screenshot (uniform-colour rectangles whose length changes with one end fixed) and tracks them |
| `GameWindow.cs` | v6: foreground window → process, client area on screen, "is that a game" list, key-down check |
| `Ocr.cs` | v6: Windows.Media.Ocr wrapper (Windows 10/11); fails gracefully where it isn't available |
| `NumberFinder.cs` | v6: finds on-screen numbers (ammo, gold, XP, stats) in memory from OCR readings, confirms with one +1 write |
| `MovementFinder.cs` | v6: finds the player's position from walking / standing / jumping, confirms the height with one push |
| `MainForm.Assistant.cs` | v6: the Assistant page (switch, options, found list, what it sees, feed) |
