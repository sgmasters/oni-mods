# Changelog

## 1.0.1
- **Dupe Brain Boost:** new option "Fix lost keyboard/mouse input" (on by default, under 6. Game fixes).
  If the game stops reading keyboard and mouse while its window is focused (a missed focus event
  after Alt-Tab or an overlay), input is turned back on and a line is written to the log.
- **Safe Dupes:** every duplicant death is written to `Player.log` as a `[SafeDupes] DEATH` line with the
  cause, location, gas/liquid and temperature at head height, last breath / health / calories / stress,
  the current errand, and whether Safe Dupes saw the dupe as trapped. The minute summary gains `deaths=N`.
- Release zips now use `/` in their paths, so they extract correctly on Mac and Linux as well.
- Dupe Traffic Heatmap: no changes (version raised to keep the set in step).

## 1.0.0
- First release of Dupe Brain Boost, Safe Dupes and Dupe Traffic Heatmap.
