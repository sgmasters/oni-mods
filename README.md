# Oxygen Not Included mods for big colonies

Three independent mods, made for colonies of 100-200 duplicants and high game speeds
(for example with Customizable Speed). Each one can be enabled on its own.

| Mod | What it does |
|---|---|
| **Dupe Brain Boost** | The base game lets only one duplicant choose an errand per frame, so in big colonies dupes stand still for many seconds and can scald or suffocate. This mod lets more dupes decide per frame within a CPU time budget, puts dupes in danger first, and runs path-finding on more background threads. CPU profiles adapt it to 1, 2, 4, 8 or more cores. Works alongside Fast Track: it detects Fast Track and switches off the parts Fast Track already covers. |
| **Safe Dupes** | Dupes stop trapping themselves: they won't build from the side that gets sealed in, or remove the floor or ladder they need to get out. A dupe that is already trapped gets an automatic dig or deconstruct order to open a way out. Hurt dupes skip errands in scalding, freezing, radioactive or airless spots. |
| **Dupe Traffic Heatmap** | An overlay that shows where duplicants walk and climb most, so you can see where plastic ladders, fire poles, better tiles or transit tubes save the most time. |

Every setting is available from the **Options** button next to the mod in the Mods menu
(changes apply after a restart).

## Requirements
- Oxygen Not Included build 744825 or newer. Tested with Spaced Out; the base game should work too.

## Install
1. Download the zip for the mod you want from the [Releases](../../releases) page.
2. Extract it into `Documents\Klei\OxygenNotIncluded\mods\local\`, so you get, for example,
   `mods\local\SafeDupes\SafeDupes.dll`.
3. Start the game, open **Mods**, enable the mod, and restart.

## Diagnostics
Each mod writes a short stats line to the game log once a minute:
`%USERPROFILE%\AppData\LocalLow\Klei\Oxygen Not Included\Player.log`
(search for `[DupeBrainBoost]`, `[SafeDupes]`). Please attach that file to bug reports.

## Building from source
Needs the .NET SDK (any recent version) and an installed copy of the game.

```
dotnet build -c Release DupeBrainBoost\DupeBrainBoost.csproj
```

The build references the game's DLLs from `C:\Program Files\Epic Games\OxygenNotIncluded`;
pass `-p:GameDir="<your game folder>"` if yours is elsewhere. Release builds copy the mod into your
local mods folder. `package.ps1` builds all three and creates release zips in `dist\`.

The options dialog uses [PLib](https://github.com/peterhaneve/ONIMods) by Peter Han (MIT),
merged into each DLL with ILRepack.

## License
MIT, see [LICENSE](LICENSE).
