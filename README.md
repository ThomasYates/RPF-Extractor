# RPF Extractor

Windows tool that unpacks every GTA V `.rpf` in a folder, recursing through nested archives, and outputs one of two things:

- **Dump mode (FiveM toggle off):** an `extracted` folder is created in the export location. Inside it, each archive becomes a folder with the original layout (`dlc.rpf` → `dlc.rpf\x64\...`, and nested `.rpf` files become folders too). Binary files are decrypted and decompressed. Resources (`.ydr`, `.ytd`, `.ymap`, …) are written as standard RSC7 files that open in OpenIV and CodeWalker.
- **FiveM map mode (toggle on):** a drag-and-drop resource:
  ```
  <resource name>/
    fxmanifest.lua          (this_is_a_map + DLC_ITYP_REQUEST for every .ytyp)
    stream/<source archive>/*.ymap .ytyp .ydr .ydd .yft .ytd .ybn .ynv .ynd .ycd
  ```
  Peds and clothing, vehicles, weapons, animations and audio are left out. FiveM streams by file name, so when two files share a name only the first is kept; `patch`/`update` archives are processed first so their copies win. Loose map files in the source folder are included as well.

## Download

Grab the latest build from the [Releases](../../releases) page. It is a single `.exe` with no install and no .NET runtime needed.

| Release | Built from | File |
|---|---|---|
| `latest` | `main` branch (stable) | `RPFExtractor.exe` |
| `beta` | `beta` branch (newest changes, may be unstable) | `RPFExtractor-beta.exe` |

Both are rebuilt automatically whenever their branch is updated.

### Meta / XML / INI files (FiveM mode)

Every `.meta`, `.xml`, `.ini`, `.rel`, `.ymt` and `.ymf` found is checked to see whether it helps the map. Each file is identified from the DLC's own `content.xml` where one exists. Otherwise it's identified by its contents.

| Found | What happens |
|---|---|
| `gtxd.meta` (texture parenting) | copied to `data/` and registered as `GTXD_PARENTING_DATA` (fixes missing/grey textures) |
| `interiorproxies.meta` | registered as `INTERIOR_PROXY_ORDER_FILE` |
| timecycle modifier XML | registered as `TIMECYCLEMOD_FILE` |
| `.dat151/.dat15/.dat54/.dat10.rel` (interior audio) | registered as `AUDIO_GAMEDATA` / `AUDIO_DYNAMIXDATA` / `AUDIO_SOUNDDATA` / `AUDIO_SYNTHDATA` |
| `<number>.ymt` (interior audio occlusion) | streamed |
| vehicle/ped/weapon metas, `.ini`, `content.xml`, `setup2.xml`, `.ymf`, video/streaming lists, speech | moved to `_not_used/` |
| anything unrecognised, CodeWalker `.ymap.xml`/`.ytyp.xml`, scenario/door `.ymt` | moved to `_not_used/` and flagged "worth a look" |

Registered files get both a `files {}` entry and a `data_file` line in `fxmanifest.lua`. `meta-report.txt` explains the decision for every file.

The progress bar shows files, data, speed and time remaining. **Pause/Resume** and **Stop** take effect within about 1 MB of I/O.

## Z-fight fix (Off / On / Both)

This finds two copies of the same building in the **exact same place**: within 2 cm, with the same rotation (±1.6°) and the same scale.

- **Same model twice:** the extra copy is deleted.
- **One copy is a grey box, the other is textured:** the grey copy is deleted. "Grey" means *provably* untextured. The model is in the output, has no embedded textures, and its texture dictionary exists neither in the output nor in the base game.

Things it deliberately leaves alone:
- LOD/SLOD models overlapping their detailed version. The LOD system works that way, and Rockstar's `_lod` models are real textured models.
- Decal and detail overlays that share a building's pivot.
- Scripted IPL states (e.g. `_burnt` / `_unburnt`), which are never loaded together.

A ymap is only edited when no other map can be linking to its entities by index. Anything unsafe to change is listed as "found but not changed". Every run writes `zfight-report.txt` explaining what was removed and why.

**Both** writes an untouched copy and a fixed copy side by side, so you can compare:
- FiveM mode: `my_map` and `my_map_zfix` (FiveM resource names can't contain spaces)
- Folder dump: `extracted` and `extracted (ZFightFix)`

The base-game texture/model index is built from the game's archive tables the first time (about 2 s) and cached in `%AppData%\RpfToFiveM\game-assets.json`. Without a GTA V folder the check is more cautious. It still removes exact duplicates, but never assumes a model is grey when its textures might come from the base game.

## Crash recovery

Each file is recorded in a journal (`%AppData%\RpfToFiveM\job\`) as soon as it has been fully written. If the app crashes, is killed, or is closed or stopped mid-run, the next launch shows a banner offering to **Resume job**. Resuming skips files that were already finished and redoes the file that was cut off. A run that finishes clears the journal automatically.

## Logs

Every session is appended to `logs.txt` next to the exe (or `%AppData%\RpfToFiveM\logs.txt` if that folder isn't writable). **Open log** in the app opens it. The file keeps the last ~5 MB; older content moves to `logs.old.txt`.

Folder paths are removed so the log can be shared when reporting a problem:
- the chosen folders appear as `<source>`, `<export>` and `<gta>`
- the user profile appears as `<home>` and the Windows user name as `<user>`
- any other drive or network path appears as `<path>`, keeping only the file name

Paths inside archives (such as `dlc.rpf/x64/levels/...`) are kept, since they show which file a problem came from. Crashes and failed jobs include the full error details.

## Encryption

| Archive type | Needs GTA V folder? |
|---|---|
| OPEN / unencrypted (OpenIV, CodeWalker, most mods) | No |
| AES / NG (straight from the game or DLC packs) | Yes |

The tool contains no keys. When a GTA V folder is set, the AES key is found inside your own `GTA5.exe` / `GTA5_Enhanced.exe` by its SHA1 hash. That key then unlocks the bundled NG tables. The AES key is cached in `%AppData%\RpfToFiveM\settings.json`, so the executable is only scanned once.

**GTA V Enhanced note:** Enhanced edition assets use the gen9 formats (e.g. ydr v159, ytd v5), and FiveM can't load them. The tool warns you when it extracts them. For FiveM, extract from a Legacy install, or convert them with CodeWalker's Gen9 converter.

## Build

```bash
dotnet test
dotnet publish src/RpfToFiveM.App -c Release -o dist
```

This produces `dist/RPFExtractor.exe`, a self-contained single file that doesn't need a .NET install. Requires the .NET 8 SDK on Windows.

## Branches and releases

- `main`: stable. Every push runs the tests, builds the exe and replaces the `latest` release.
- `beta`: work in progress. Every push does the same for the `beta` pre-release.

Changes normally land on `beta` first and are merged into `main` once they're tested. The workflow lives in `.github/workflows/build.yml`.

## Layout

- `src/RpfToFiveM.Core`: RPF7 reader, AES/NG decryption, extraction engine, FiveM rules
- `src/RpfToFiveM.App`: WPF UI
- `tests/RpfToFiveM.Tests`: xUnit tests. They use synthetic RPF7 archives built by `Fixtures/RpfBuilder.cs`, plus one integration test that runs only when GTA V is installed.

## Third-party notice

`src/RpfToFiveM.Core/Resources/magic.dat`, the AES key hash, and the NG cipher layout come from [CodeWalker](https://github.com/dexyfex/CodeWalker) (MIT License, Copyright (c) 2017 dexyfex; portions Copyright (c) 2015 Neodymium). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
