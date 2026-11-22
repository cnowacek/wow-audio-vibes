# WoW Audio Vibes

Music for every corner of Azeroth: Spotify that follows you around World of Warcraft (Forever, retail or Classic).

There are two parts:
- **Audio Vibes**, the WoW addon. It has a player bar and a config window, and gives every city, zone, dungeon, raid and PvP map its own playlist.
- **WoW Audio Vibes**, a small Windows tray app (the *companion*) that controls Spotify.

Addons can't use the network, so the addon plays tiny near-ultrasonic chirps (17–19 kHz, −45 dBFS). The companion captures **only WoW's audio stream**, decodes them and drives Spotify.

```
Audio Vibes addon ─PlaySoundFile(chirp.ogg)─▶ WoW audio ─WASAPI process loopback─▶ WoWAudioVibes.exe ─▶ Spotify
        └──────── saved playlists (AudioVibes.lua, written on /reload or logout) ─────────┘
```

The signal channel is one-way: the addon only sends. That's why the bar shows the location's playlist and the last signal *sent*, never a track title. The tray icon is where you see the connection working. The addon never reads combat-restricted data. See [`protocol/PROTOCOL.md`](protocol/PROTOCOL.md) and [`NOTES.md`](NOTES.md).

## Requirements
- Windows 10 build 20348+ or Windows 11 (for process loopback)
- .NET 8 SDK (to build the companion), Python 3.10+ (only to regenerate sounds/icons)
- Spotify desktop app. Location playlists, Hype and Like need Spotify Premium plus a Spotify developer app (below).

## 1. Install the addon
```powershell
.\scripts\link-addon.ps1                    # junction into ...\_forever_\Interface\AddOns\AudioVibes
.\scripts\link-addon.ps1 -Flavor _retail_   # or another client
```
Or copy `addon/AudioVibes` into `World of Warcraft\_forever_\Interface\AddOns\`.

> **Restart WoW fully** (exit to desktop) after installing or updating. `/reload` doesn't pick up new sound or texture files.

## 2. Build and run the companion
```bash
cd companion
dotnet build -c Release
dotnet test
```
Run `companion\src\AudioVibes.App\bin\Release\net8.0-windows10.0.19041.0\WoWAudioVibes.exe`, or tick *Start with Windows* in its tray menu.

The tray dot shows the state: **grey** WoW not running · **yellow** attached, waiting for the addon · **green** addon heard · **red** error.

## 3. Connect Spotify (playlists, Hype, Like; Premium)
1. https://developer.spotify.com/dashboard → *Create app* → tick **Web API**. You don't need the Web Playback SDK.
2. Redirect URI: `http://127.0.0.1:8888/callback` exactly. `localhost` is rejected; check the dashboard didn't rewrite it.
3. Tray → *Open config*, set `spotify.clientId` and `"useWebApi": true`, then save.
4. Tray → *Connect Spotify*. The menu then shows "Spotify: connected as <you>".

Play/pause, next/previous and volume/ducking work without Premium, through Windows' media controls and Spotify's per-app volume.

## In game
**Player bar** (drag by its header; the chevron collapses it to a mini bar). It shows the location, its playlist with a *Default*/*Custom* tag, the controls (⏮ ⏯ ⏭ · volume − + · ♥ like · ⚡ hype · ⚙ settings), and "Signal sent: … · 4s ago" with an **Auto-switch** checkbox. Shift-click ⚡ to go back to the location playlist. A "Now entering …" toast appears when the playlist changes.

**Config window** (`/vibes`, or the gear):
- **Locations:** every location with filters (Cities / Zones / Dungeons / Raids / PvP / Custom only) and search, a *You are here* card, and **Link** / **Play** / **Change** / **Reset** per row. **Link** (and *Copy link* on the card) shows the playlist's `open.spotify.com` link already selected; press Ctrl+C, since addons can't write the clipboard themselves. Forever-only zones show up as *New in Forever* once you've visited them.
- **Change playlist:** paste a Spotify playlist or album link (it's checked as you type), give it a display name, and apply it to this location or to *every <type> without a custom playlist*. You can also turn on shuffle.
- **Settings:** music, events (lower music on queue pop and death, level-up fanfare), player bar options, a signal check with **Turn on** fixes and **Send test signal**, recently sent signals, the Hype playlist and your keybinds.

**Custom playlists reach the companion through the addon's saved settings**, which WoW only writes on `/reload` or logout. Rows show *Pending reload* and the footer offers **Reload UI** until then.

**Going the other way**, the companion writes `addon/AudioVibes/Resolved.lua` with the playlist it has chosen for every location: defaults found by search, and playlists set in its Zone playlists window. WoW reads that on login and `/reload`, so the addon can show real playlist names and links for default playlists too. Links for defaults appear once Spotify is connected in the companion and you've done a `/reload`.

Commands: `/vibes` · `/vibes show` · `/vibes settings` · `/vibes zone` · `/vibes test` · `/vibes next|prev|play|vol+|vol-|like|hype`. Key bindings: *Options → Keybindings → AddOns → Audio Vibes*.

## Which playlist plays where
For each location the companion picks, in order:
1. your in-game playlist for that location
2. a per-zone playlist set in the companion (tray → *Zone playlists…*)
3. your in-game "every <type>" playlist
4. a per-vibe playlist set in the companion
5. the default for the location's **vibe** (16 vibes such as royal capital, haunted lands, dungeon crawl, raid night)

Each default comes from a curated Spotify search that runs once and is then remembered. The zone → vibe list lives in [`protocol/zones.json`](protocol/zones.json), which is shared by the addon and the companion. Moving between locations that resolve to the same playlist doesn't restart it.

## Companion config
`%APPDATA%\WoW Audio Vibes\config.json` is created on first run and reloaded whenever it's saved. If you used the companion when it was called Murmur, it copies your config and Spotify login over from `%APPDATA%\Murmur` once.

Useful keys:
- `wowClientFolder`: the WoW client folder that contains `WTF\`. It's detected automatically.
- `eventMap`: what each `EVT_*` does: `PLAY_PAUSE NEXT PREV VOL_UP VOL_DOWN DUCK_ON DUCK_OFF LIKE_CURRENT PLAYLIST_SLOT FANFARE NONE`. `DUCK_ON` takes `autoRevertMs`.
- `playlistSlots`, `zonePlaylists`, `vibePlaylists`, `duckLevel`, `volumeStep`, `fanfareSeconds`, `detection`.

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
scripts/    link-addon.ps1
```
