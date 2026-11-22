# WoW Audio Vibes

Music for every corner of Azeroth: music that follows you around World of Warcraft (Forever, retail or Classic), from Spotify, free internet radio, or music folders on your PC.

There are two parts:
- **Audio Vibes**, the WoW addon. It has a player bar and a config window, and gives every city, zone, dungeon, raid and PvP map its own playlist.
- **WoW Audio Vibes**, a small Windows tray app (the *companion*) that plays the music. It controls Spotify, and plays radio streams and folders itself.

Addons can't use the network, so the addon plays tiny near-ultrasonic chirps (17–19 kHz, −45 dBFS). The companion captures **only WoW's audio stream**, decodes them and starts the right music.

```
Audio Vibes addon ─PlaySoundFile(chirp.ogg)─▶ WoW audio ─WASAPI process loopback─▶ WoWAudioVibes.exe ─▶ Spotify / radio / folder
        └──────── saved playlists (AudioVibes.lua, written on /reload or logout) ─────────┘
```

The signal channel is one-way: the addon only sends. That's why the bar shows the location's playlist and the last signal *sent*, never a track title. The tray icon is where you see the connection working. The addon never reads combat-restricted data. See [`protocol/PROTOCOL.md`](protocol/PROTOCOL.md) and [`NOTES.md`](NOTES.md).

## Install (players)
You need Windows 10 build 20348+ or Windows 11 (for process loopback). That's all for the free radio defaults and your own music folders. Spotify is optional: it needs the Spotify desktop app, Premium, and a Spotify developer app (step 3).

Download both files from the [latest release](https://github.com/cnowacek/wow-audio-vibes/releases/latest):

1. **The addon:** `AudioVibes-x.y.z.zip`. Unzip it into `World of Warcraft\_forever_\Interface\AddOns\` (or `_retail_`, `_classic_era_` …), so you end up with `AddOns\AudioVibes\AudioVibes.toc`. Then **restart WoW fully**: exit to desktop, because `/reload` doesn't pick up new sound files.
2. **The companion:** `WoWAudioVibes-win-Setup.exe`. Run it. It installs for your Windows user only (no admin needed) into `%LOCALAPPDATA%\WoWAudioVibes`, adds a Start menu entry and starts the tray app. Tick *Start with Windows* in its tray menu if you want it always running.
   - It **updates itself**. Every 6 hours it checks this repo's releases and downloads a newer version in the background. The update installs when you quit, or straight away with tray → *Restart to update*. Tray → *Check for updates* checks right now. Set `"autoUpdate": false` in the config to turn this off.
   - Windows SmartScreen may warn about an unrecognised app, because the installer isn't code-signed. Click *More info → Run anyway*.
   - Uninstall it from *Settings → Apps*. Your settings in `%APPDATA%\WoW Audio Vibes` are kept.
   - `WoWAudioVibes-win-Portable.zip` is the same app without an installer. It doesn't auto-update.
   That's enough to start: every location plays a free internet radio station picked for its vibe.
3. **Connect Spotify** (optional; Premium). Spotify playlists become the defaults, and Like works:
   Open the companion's **Settings** (double-click the tray icon, or tray → *Settings…*) → **Spotify** tab. It walks you through:
   1. Go to https://developer.spotify.com/dashboard → *Create app* → tick **Web API**. You don't need the Web Playback SDK.
   2. Set the redirect URI to `http://127.0.0.1:8888/callback` exactly (there's a Copy button). `localhost` is rejected; check the dashboard didn't rewrite it.
   3. Paste the app's Client ID.
   4. Click *Connect Spotify…*. The tab and the tray menu then show "Spotify: connected as <you>".

   Why your own developer app: Spotify limits a developer app in Development Mode to its owner plus 5 people they add by hand. Anything more needs Spotify's approval, which in practice means a registered business with a large user base. So each player brings their own.

With Spotify, play/pause, next/previous and volume work through Windows' media controls and Spotify's per-app volume. With radio and folders, the companion plays the music itself and handles those keys.

## Music sources
Anywhere you can set a playlist (the in-game *Change* dialog, the companion's *Zone playlists* window, `config.json`) you can use:

| Source | Example | Notes |
|---|---|---|
| Spotify playlist or album | `https://open.spotify.com/playlist/…` | Needs Spotify connected (step 3). |
| Internet radio | `https://ice1.somafm.com/thistle-128-mp3` | Any MP3/AAC stream URL, or a `.pls`/`.m3u` link. Next/previous don't apply; pause rejoins the stream live. |
| A folder of music | `C:\Music\Haunted` | Plays MP3, M4A/AAC, FLAC, WAV, WMA and Ogg Vorbis, subfolders included, in order or shuffled. Coming back within 24 h resumes the same track and position. In the companion window, *Choose folder…* picks one. |

YouTube links aren't supported. YouTube has no playback API for this, and its terms don't allow background audio-only play.

**Default stations.** Without Spotify, every vibe has a Relaxed and a Locked-in station: [Radio Rivendell](https://www.radiorivendell.com) (fantasy music), [Rainwave](https://rainwave.cc) (game music, covers, OverClocked Remix), [Radio Swiss Classic](https://www.radioswissclassic.ch), and [SomaFM](https://somafm.com) channels (Thistle Radio, Drone Zone, The Dark Zone, Doomed, Groove Salad, Metal Detector). They're free and listener-supported; consider supporting them. The list is in [`protocol/zones.json`](protocol/zones.json) under `stations`. `"zoneMusic": { "defaultSource": "auto" }` picks Spotify when connected and radio otherwise. You can also set it to `"spotify"`, `"radio"` or `"off"`.

The tray dot shows the state: **grey** WoW not running · **yellow** attached, waiting for the addon · **green** addon heard · **red** error.

## Develop
You need the .NET 10 SDK (pinned in `global.json`), plus Python 3.10+ for the tests and for regenerating sounds and icons.

```powershell
.\scripts\link-addon.ps1                    # junction addon/AudioVibes into ...\_forever_\Interface\AddOns
.\scripts\link-addon.ps1 -Flavor _retail_   # or another client
cd companion; dotnet build -c Release; dotnet test
```
`$env:AUDIOVIBES_NET_TESTS = "1"; dotnet test` also checks that every built-in radio station still streams and decodes.

Run `companion\src\AudioVibes.App\bin\Release\net10.0-windows10.0.19041.0\WoWAudioVibes.exe`. A build from `bin\` never updates itself; only the installed app does.

The companion writes `addon/AudioVibes/Resolved.lua` (see below), and through the junction that's the repo's copy. The repo keeps an empty stub of it. Run this once so git ignores your local changes:
```powershell
git update-index --skip-worktree addon/AudioVibes/Resolved.lua
```

## Make a release
```powershell
.\scripts\release.ps1 -Version 1.3.0         # builds into dist\, uploads nothing
```
This runs the companion tests and publishes a self-contained `win-x64` build. It then packs it with [Velopack](https://velopack.io) (pinned in `.config/dotnet-tools.json`) into `dist\releases\`: `Setup.exe`, the full and delta `.nupkg` update packages, `releases.win.json` and the portable zip. It also zips the addon into `dist\AudioVibes-1.3.0.zip`, with the stub `Resolved.lua` and `## Version` set.

**To publish**, push a tag. The [release workflow](.github/workflows/release.yml) runs the tests, builds everything on GitHub Actions and creates the GitHub release with all the files attached:
```powershell
git tag -a v1.3.0 -m "What changed (becomes the release notes)"
git push origin v1.3.0
```
Or run it locally with `.\scripts\release.ps1 -Version 1.3.0 -Publish`. That needs `$env:GITHUB_TOKEN` (contents: write) and the `gh` CLI.

Notes on releasing:
- Versions must go up: `1.3.0`, `1.3.1` … A `-beta.1` suffix makes a pre-release, which installed apps ignore.
- Every file in `dist\releases` has to be on the release. Installed apps read `releases.win.json` from the **latest** release, so don't delete old releases' assets while people might still be on them.
- If you fork, change `Updater.RepoUrl` (companion) and `-RepoUrl` (script), or installs will keep updating from this repo.
- The addon doesn't auto-update. Players re-download the zip, or use an addon manager if you also publish to CurseForge or Wago.

## In game
**Player bar** (drag by its header; the chevron collapses it to a mini bar). It shows the location, its playlist with a *Default*/*Custom* tag, the controls (⏮ ⏯ ⏭ · volume − + · ♥ like · ⚡ hype · ⚙ settings), and "Signal sent: … · 4s ago" with an **Auto-switch** checkbox. Shift-click ⚡ to go back to the location playlist. A "Now entering …" toast appears when the playlist changes.

**Config window** (`/vibes`, or the gear):
- **Locations:** every location with filters (Cities / Zones / Dungeons / Raids / PvP / Custom only) and search, a *You are here* card, and **Link** / **Play** / **Change** / **Reset** per row. **Link** (and *Copy link* on the card) shows the playlist's `open.spotify.com` link already selected; press Ctrl+C, since addons can't write the clipboard themselves. Forever-only zones show up as *New in Forever* once you've visited them.
- **Change playlist:** paste a Spotify playlist or album link, a radio stream URL, or a folder path (it's checked as you type), give it a display name, and apply it to this location or to *every <type> without a custom playlist*. You can also turn on shuffle.
- **Settings:** music, events (lower music on queue pop and death, level-up fanfare), player bar options, a signal check with **Turn on** fixes and **Send test signal**, recently sent signals, the Hype playlist and your keybinds.

**Custom playlists reach the companion through the addon's saved settings**, which WoW only writes on `/reload` or logout. Rows show *Pending reload* and the footer offers **Reload UI** until then.

**Going the other way**, the companion writes `addon/AudioVibes/Resolved.lua` with the playlist it has chosen for every location: defaults found by search, and playlists set in its Zone playlists window. WoW reads that on login and `/reload`, so the addon can show real playlist names and links for default playlists too. Links for defaults appear once Spotify is connected in the companion and you've done a `/reload`.

Commands: `/vibes` · `/vibes show` · `/vibes settings` · `/vibes zone` · `/vibes test` · `/vibes next|prev|play|vol+|vol-|like|hype`. Key bindings: *Options → Keybindings → AddOns → Audio Vibes*.

## Vibe modes: Relaxed and Locked-in
Every location has two playlists: a **Relaxed** one (exploring, questing, hanging around town) and a **Locked-in** one (running content, focused, louder).
- **Overall mode:** *Auto* (default), *Relaxed* or *Locked-in*. Set it with the mode pill on the player bar, in Settings, or with the *Switch vibe mode* keybind. Auto means Locked-in in dungeons, raids and PvP, and Relaxed everywhere else.
- **Per location:** in the Change playlist dialog, fix a location to one mode (say, always Relaxed in Orgrimmar) or leave it following the overall mode. The dialog has a tab for each mode's playlist.
- **Resume:** switching back to a playlist you left within the last 24 hours picks up the same track and position (needs the Web API connection). Turn it off in Settings (*Pick up where I left off*).

## Which playlist plays where
For each location, in the mode that applies there, the companion picks in this order:
1. your in-game playlist for that location and mode
2. a per-zone playlist set in the companion (tray → *Zone playlists…*); `"map:1454@relaxed"` is for one mode, `"map:1454"` for both
3. your in-game "every <type>" playlist for that mode
4. a per-vibe playlist set in the companion (`"spooky@locked"` or `"spooky"`)
5. the default for the location's **vibe** in that mode (17 vibes such as royal capital, haunted lands, dungeon crawl, raid night, each with a Relaxed and a Locked-in flavour)

With Spotify connected, each default comes from a curated Spotify search that runs once and is then remembered. Without Spotify, the default is the vibe's radio station. The zone → vibe list lives in [`protocol/zones.json`](protocol/zones.json), which is shared by the addon and the companion. Moving between locations that resolve to the same playlist doesn't restart it.

## Companion settings
Everything is in the **Settings** window (double-click the tray icon):
- **General:** start with Windows, automatic updates, the WoW client folder.
- **Music:** location music on/off, where default music comes from (Automatic, Spotify, free radio, or nothing), volume step, how far to lower the music, fanfare length.
- **Spotify:** guided setup, connect and disconnect.
- **Game events:** what queue pops, deaths, level-ups and other events do, plus the playlist slots.
- **Advanced:** signal-detection thresholds and WoW program names.

The settings are stored in `%APPDATA%\WoW Audio Vibes\config.json`, which is reloaded whenever it's saved. You can still edit it by hand (Settings → Advanced → *Open config.json*). Settings added in newer versions are filled in with their defaults on start. If you used the companion when it was called Murmur, it copies your config and Spotify login over from `%APPDATA%\Murmur` once.

The keys in `config.json`:
- `wowClientFolder`: the WoW client folder that contains `WTF\`. It's detected automatically.
- `eventMap`: what each `EVT_*` does: `PLAY_PAUSE NEXT PREV VOL_UP VOL_DOWN DUCK_ON DUCK_OFF LIKE_CURRENT PLAYLIST_SLOT FANFARE NONE`. `DUCK_ON` takes `autoRevertMs`.
- `playlistSlots`, `zonePlaylists`, `vibePlaylists`, `duckLevel`, `volumeStep`, `fanfareSeconds`, `detection`.
- `zoneMusic.defaultSource`: `auto` (default), `spotify`, `radio` or `off`.
- `autoUpdate`: `true` by default (installed app only).
- The radio/folder player's volume is remembered in `player.json`.
- Set the environment variable `AUDIOVIBES_HOME` to use another settings folder; the tests do this.

## Debugging and tuning
- Tray → *Open debug window*: live levels for every tone, a log of decoded and rejected signals and of playlist decisions, and **Record 60 s** of WoW audio (saved to `%APPDATA%\WoW Audio Vibes\recordings`).
- Console tool: `dotnet run --project src/AudioVibes.Cli -- levels | decode | record out.wav 60 | file some.wav`.
- Put recorded gameplay WAVs in `companion/tests/AudioVibes.Decoder.Tests/fixtures/`. `dotnet test` then reports detection rates and checks for zero false positives on real gameplay.
- Too quiet or not detected? `python generate.py --base-hz 15000`, then put the same value in `protocol/protocol.json`, rebuild, and restart WoW.
- Logs are in `%APPDATA%\WoW Audio Vibes\logs`.

## Regenerating assets
```bash
cd generator
pip install -r requirements.txt
python generate.py --plot     # 63 command + 512 location chirps, Commands.lua, Locations.lua; Vorbis round-trip check
python icons.py               # UI icons (TGA) from the design's stroke icons
python app_icon.py            # the companion's app/installer icon (app.ico)
python -m pytest tests        # protocol tests + addon smoke tests in Lua 5.1 against a mocked WoW API (needs lupa)
```

## Layout
```
protocol/   protocol.json (signal format), zones.json (locations → vibes → defaults), PROTOCOL.md
generator/  generate.py, vibes_proto.py (renderer + Python decoder port), icons.py, tests (incl. addon smoke tests)
addon/AudioVibes/  .toc, Theme/Core/Triggers/PlayerBar/PlaylistDialog/Config.lua, Bindings.xml,
                   generated Commands.lua + Locations.lua, sounds/, media/ (icons)
companion/  AudioVibes.Decoder (pure), AudioVibes.Audio (process loopback), AudioVibes.Spotify,
            AudioVibes.App (tray app WoWAudioVibes.exe), AudioVibes.Cli (vibes-cli), tests
scripts/    link-addon.ps1 (dev install), release.ps1 (installer + addon zip, optional publish)
.github/workflows/release.yml   tag v* → build and publish a GitHub release
```
