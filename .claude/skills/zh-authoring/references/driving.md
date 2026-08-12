# Driving the running game

Closing the loop that every silent bug escaped through — each was found by a human noticing
something in a match, which is the slowest detector there is.

`tools/run-logged.sh` (installed to the game dir) and `tools/zhdrive` close the loop that
every silent bug in the catalogue above escaped through — each was found by a human noticing
something in a match.

- `zhdrive log --dirs` — the additive directory list THIS build scans, from the boot log.
- `zhdrive log --errors` — distinct error shapes, deduped by digit-normalising.
- `zhdrive shot` / `zhdrive wait <regex>` — screenshot, or block until the engine logs a line.
  Waiting on the log beats sleeping a guess: boot time varies threefold with disk cache.
- `zhdrive skirmish` — launch, skip the intro, drive into a RUNNING MATCH, unattended.
- `zhdrive ui <target>` / `zhdrive pixel x y` — click or sample in the game's own 800x600
  space. Targets are expressed there, not in screen coordinates, because that is the space
  `ControlBarScheme` authors in (`ScreenCreationRes X:800 Y:600`) and it does not move when
  the window does.

Launch with **`-quickstart`** (= `-nologo -noshellmap` + no window animation). `parseNoLogo`
sets `m_playIntro = FALSE`, which is the supported way past the intro movie; pressing `esc` at
it was always a workaround and it is what made the first drives flaky.

**Every step must be CONFIRMED, never slept through.** The first scripted drive slept fixed
intervals, fired all three clicks into the intro movie and reported "in match (probably)".
The rewrite waits on a *signal* per step — a log line where one exists
(`SkirmishGameOptionsMenu.wnd`), a pixel where none does (the intro movie is not a layout, so
`MainMenu.wnd` is already pushed while the movie still owns the keyboard) — and retries the
click pair when it did not land. **It needed that retry on its very first run.**
*Accessibility is granted to the RESPONSIBLE PROCESS: Terminal.app when Claude Code runs as
its child, the `claude` binary when it runs as a daemon. Granting the wrong one is silent.*

Four traps, each of which cost a run:
- **Focus first, every attempt.** macOS eats the first click on an unfocused window. Attempt 1
  failed on EVERY drive — deterministically, and that determinism is the tell. A stolen focus
  also corrupts PIXEL READS, which is worse than a lost click because another app's colours
  come back as data rather than as an error.
- **Park the cursor centre after every click.** An RTS scrolls whenever the pointer rests near
  an edge, and a build button at the bottom of the command bar IS in the scroll margin — so
  clicking one and then pausing pans the world out from under every later world coordinate.
- **Identify a menu by COUNTING its buttons, not by probing a point.** Main menu 6, Solo Play
  submenu 7, and they are offset — so one screen's text pixel is the next screen's border.
  Each button draws a top and bottom border, so the cyan runs come in PAIRS: divide by two.
- **`esc` does not leave the Solo Play submenu**, which has only a BACK button. A retry that
  only presses `esc` waits forever on a screen it cannot leave.

`zhdrive verify-pack` is the payoff, and `e2e.sh` gate 25 runs it behind **`ZH_PLAY=1`**
(opt-in: it needs the install, Accessibility and ~2 minutes). It asserts what only a running
match can: the portrait and the build button are SATURATED authored icons rather than the dark
panel a dangling `MappedImage` renders as, the two address DIFFERENT cells, and clicking build
CHARGES the player. Proven in both directions — deleting the emitted `MappedImages` file makes
all three fail, and the portrait reads pure black.
*The structure is found by COLOUR, never by coordinate: the start position is randomised, so
the same map put the factory at game (512,139) one run and (297,165) the next.*

**Never read or click game-space coordinates unless the game is FRONTMOST.** Chrome took focus
mid-sequence and `find` returned a point inside ITS window, after which every click went into a
browser. A pixel read of the wrong app comes back as plausible DATA rather than as an error,
which makes reading the more dangerous half. `shot` is exempt — capturing the screen is always
safe and is how you find out what went wrong.

**A failed drive must name WHO failed it.** `zhdrive` records where it parked the cursor and
checks it before diagnosing anything, because a wedged shell and a stray human click look
identical in a screenshot and have opposite fixes — one wants a relaunch, the other wants
hands off and a retry. This driver blamed the engine twice without being able to rule the
other out, which is not a diagnosis. *Every place that moves the cursor must RECORD where it
left it — the guard's first outing blamed a human for the driver's own focus click on a retry,
and a guard that cries wolf is worse than none.*

**Installing a pack must UNINSTALL the previous one.** `rts compile` writes `MANIFEST.txt` and
prints the `rm`-by-manifest that undoes an install. Since faction sides became pack-prefixed,
packs no longer overwrite each other — they COEXIST, the lobby lists several factions, and a
match plays whichever is default. That silently tests the wrong pack, and it cost an hour
twice: once chasing a model override that was never broken, once a build bar that was never
missing. Retail's `PlayerTemplate` is a single file, so anything inside
`Data/INI/PlayerTemplate/` is a pack and the installed set is discoverable without the manifest
— which is how the e2e gate cleans before it installs, and why it refuses to run with more
than one pack present.

*The visibility guard on `zhdrive` is a WARNING, not a gate.* Two attempts at a hard check —
frontmost, then a cyan content probe — both produced false refusals that blocked real work:
focus legitimately sits with the terminal that launched the command, and the cyan landmarks
move with window size and camera. The genuine hazard is real but rare (Chrome COVERING the
game, so a read returns another app's colours as plausible data), so `find_structure`
re-probes its own answer instead and the rest warns.

**Observe needs no permission and answers every LOAD-TIME question**, which is where every
bug so far has actually lived. Act buys "click build and see" and nothing before it.
*Do not use `osascript -e 'tell application "Finder" to get bounds of window of desktop'` to
find the screen size — the usual recipe, and it HUNG this tool with no timeout. `zhdrive`
uses `system_profiler`. Coordinates are physical pixels in a screenshot and LOGICAL points to
cliclick; on this Retina panel they differ by 2x.*
