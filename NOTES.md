# Verification notes (spec §8)

Status as of 2026-11-22. ✅ = verified on this machine · ⏳ = needs you in game · 📄 = from docs.

| Item | Result |
|---|---|
| Forever `## Interface` | ✅ **16001**. Read from installed Forever addons (AtlasLootClassic etc. use `16001`). Product code `wowf`, build 1.60.1.71037. Confirm in game with `/dump select(4, GetBuildInfo())`. |
| Forever client folder | ✅ `C:\Program Files (x86)\World of Warcraft\_forever_\Interface\AddOns` |
| Forever exe name | ✅ **`Wow.exe`** (the same name as retail). The default list covers it. If you run retail and Forever at the same time, the companion attaches to whichever `Wow.exe` it finds first. |
| Windows build supports process loopback | ✅ Windows 11 build 26200 (needs ≥ 20348). |
| Process-loopback capture + live decode | ✅ Tested with a stand-in process (PowerShell `SoundPlayer` looping 4 chirps): `vibes-cli decode --pid <pid>` got a 48 kHz stereo float stream and decoded 19/19 frames in real time. WoW itself is still ⏳. |
| Decoder tests | ✅ 76 passed, 2 skipped (real fixtures not recorded yet). Synthetic in-band noise at −85 dBFS: 100% at −45 and −55 dBFS, ~45% at −65 dBFS; 0 wrong codes; 0 false positives in 10 min. At −70 dBFS in-band noise, −45 still gets 98% but −55 drops to 0%. The preamble tones are 6 dB below peak, and the 12 dB preamble threshold is the limiting factor. |
| Sound CVars present in Forever | ✅ `Sound_MasterVolume` is in `_forever_\WTF\Config.wtf` (0.6). `Sound_EnableAllSound` and `Sound_EnableSoundWhenGameIsInBackground` are standard. ⏳ Confirm the addon's warning fires when they're off. |
| Vorbis survives the band | ✅ Offline: libsndfile 1.2.2 at quality 1.0, worst tone loss **+0.01 dB**, worst symbol SNR 38 dB. All 63 codes decode in the Python port. |
| Custom `PlaySoundFile` paths and the `"Master"` channel | ✅ Login PING decoded live from Forever (tray went green with no manual test). So the addon loads, the custom `.ogg` path plays on `"Master"`, and the 17–18.8 kHz band at −45 dBFS gets through WoW's mixer to the loopback tap. If it ever fails, `/vibes debug` then `/vibes test` prints `[PlaySoundFile refused]` when WoW rejects the file. |
| `PLAYER_REGEN_DISABLED/ENABLED`, `LFG_PROPOSAL_SHOW`, `UPDATE_BATTLEFIELD_STATUS` | ⏳ Registered through `C_EventUtils.IsEventValid`/`pcall`. Check with `/dump AudioVibes.registeredEvents`. The combat trigger is **off by default** (`/vibes trigger combat`). |
| Band survives WoW's mixer/resampler (live) | ⏳ `vibes-cli levels` while running `/vibes test` repeatedly. All 3 data bins and both preamble bins should jump ≥ 30 dB above the floor. |
| Lowest reliable amplitude in loud fights / audibility | ⏳ Record fixtures (debug window → Record 60 s) during raids, copy them into `companion/tests/AudioVibes.Decoder.Tests/fixtures/` and run the tests. They report detection at −45/−55/−65. A/B blind test for audibility. |
| Does WoW's Windows per-app volume affect loopback? | ⏳ Process loopback taps the app's stream **after** its session volume according to most reports, so assume yes. Turn WoW down in the Volume Mixer and watch `vibes-cli levels`. The in-game master volume definitely scales the chirp. |
| Spotify redirect URI | 📄 `http://127.0.0.1:8888/callback`. Spotify no longer accepts `localhost`, but loopback IP literals may use http ([docs](https://developer.spotify.com/documentation/web-api/concepts/redirect_uri)). Some users report the dashboard rewriting it, so check the saved value. |
| Library save endpoint | 📄 `PUT /v1/me/library?uris=spotify:track:…` replaced the deprecated `PUT /v1/me/tracks` in Spotify's [February 2026 migration](https://developer.spotify.com/documentation/web-api/tutorials/february-2026-migration-guide). The companion tries the new endpoint first and falls back to the old one. |
| NAudio process loopback | 📄 NAudio 2.2.1 has no process-loopback API, so `AudioVibes.Audio` is a direct C# port of Microsoft's ApplicationLoopback sample. NAudio is only used for Spotify's per-app volume. |

## Zone music (added 2026-11-22)
| Item | Result |
|---|---|
| Zone frames survive Vorbis | ✅ All 512 zone files pass the round trip (worst loss +0.01 dB, worst symbol SNR 36.9 dB). |
| Zone decoding | ✅ C#: all 512 files decode through the real hi/lo pairing; 59/59 zones detected in −85 dBFS in-band noise; 0 false zones or commands in 10 min of noise. Python port: same cases. |
| Forever uses classic `uiMapID`s | ✅ ForeverDungeonJournal's data uses 1416/1426/1434/1445/1447/1449/1453… for classic zones, plus new ones like **2548** (Riverglades, from that addon's entrance text). |
| Instance ids | 📄 The classic `GetInstanceInfo()` ids in `zones.json` (36 Deadmines, 409 Molten Core…) come from memory, not from this client. ⏳ Check with `/vibes zone` inside a dungeon. A wrong id only means that dungeon shows up as "Instance N" with the dungeon vibe. |
| Forever-only dungeons | ⏳ Hall of Thanes, Ruins of Lordaeron, City of Dalaran, Excavation Site, Drowned City, Krol'dok Stronghold, Alcaz Prison, Blackmaw Hold, Shaper's Terrace: ids unknown. They appear in the Zone playlists window after a visit. Add them to `zones.json` once known. |
| Default playlists | 📄 Web searches couldn't confirm specific Spotify playlist IDs, so each vibe's default is a curated Spotify search, resolved once and cached. Pin a known-good playlist with `"uri"` in `zones.json`. |
| `C_Map.GetBestMapForUnit` / `GetMapInfo().mapType` in Forever | ⏳ Expected (modern Classic client). `/vibes zone` prints what it sees. |

## In-game UI from the design (added 2026-11-22)
| Item | Result |
|---|---|
| Runs without Lua errors | ✅ `generator/tests/test_addon_lua.py` loads every file in Lua 5.1 against a mocked WoW API. It opens every screen, clicks all 60+ controls, saves and resets playlists, and checks the signals sent. It caught 3 bugs that would have broken the UI in game: settings read before SavedVariables loaded, a nil-index on save, and a link parser that rejected normal playlist links. |
| How it looks | ⏳ The mock can't render. Check the layout, font sizes and icon tinting in game. Panels use `BackdropTemplate` with 1 px borders, so corners are square rather than the mock-up's 3–4 px radius. Icons are 64 px white TGAs (`generator/icons.py`) tinted with `SetVertexColor`. |
| Location id width | ✅ Highest known Forever uiMapID is 2548, which needs 12 bits. The transport is the two-part zone frame (16 bits: 15-bit id + instance flag), so it covers the spec's 12-bit EVT_LOCATION and has room for instance ids. "Play now" = zone frames + `PLAY_LOCATION` (code 55). |
| SavedVariables path | ✅ Companion watches `<client>\WTF\Account\*\SavedVariables\AudioVibes.lua`. The client folder comes from `wowClientFolder`, then the running `Wow.exe`'s folder, then the default Forever install. ⏳ Confirm it picks up a change after `/reload`; the debug window logs "in-game playlists loaded". |
| `PLAYER_REGEN_*` for the combat fade | ⏳ The same VERIFY as before. If they don't fire in Forever, the bar just doesn't fade. |
| Spec gaps I filled | Added a **PvP** filter button (the spec's open comment). The Help tab had no design, so it's plain text in the same style. "Play a fanfare on level up" plays a "Level-up fanfare" vibe for `fanfareSeconds` (30), then returns to the location playlist. |
| Naming | The design says "Murmur"; per the later rename the addon is **Audio Vibes** (`/vibes`) and the companion is **WoW Audio Vibes** (`WoWAudioVibes.exe`, `%APPDATA%\WoW Audio Vibes`, migrated from `%APPDATA%\Murmur`). |

## Fixes from in-game screenshots (2026-11-22)
| Item | Result |
|---|---|
| Locations list ran under the footer | ✅ Fixed. The list drew a fixed 11 rows where only 9 fit. Rows are now worked out from the space between the column headers and the footer (9 × 40 px in 532 − 134 − 50), and the footer is drawn above anything that scrolls. A smoke test checks the rows fit. |
| Settings "Hype & keybinds" ran into the footer | ✅ Fixed. Option rows are 36 px instead of 40, boxes are re-stacked (bottom −524 vs footer top −533), "Recently sent" shows 3 entries, and the test-signal hint is shorter so it isn't cut off. A smoke test checks it. |
| Copy-link per location | ✅ **Link** on every row and *Copy link* on the "You are here" card open a box with the `open.spotify.com` URL selected (Ctrl+C). Custom playlists' links come from the addon; default and companion-window playlists' links come from `Resolved.lua`, which the companion writes into the addon folder (debounced, only when something changed). Without a link the button is greyed out and its tooltip says why. ⏳ Check that WoW picks up the rewritten `Resolved.lua` on `/reload`; it already existed at client start, so it should. |

## Vibe modes (added 2026-11-22)
| Item | Result |
|---|---|
| Two playlists per location | ✅ Saves keep `{relaxed=…, locked=…}` per location and per type. Old single-playlist saves count for both modes (migrated in the addon, read the same way by the companion). Covered by the addon smoke tests and `ModeTests`. |
| Mode signals | ✅ `MODE_RELAXED` (56) / `MODE_LOCKED` (57) go out with every location, and on their own when the mode changes. `PLAY_LOCATION` is 55. Codes 58–63 are spare. Before the first mode signal the companion uses Auto by location type. |
| Resume | ⏳ Uses `GET /me/player/currently-playing` when leaving a playlist and `PUT /me/player/play` with `offset` + `position_ms` on return. Resume points last 24 h and are kept in `zone-state.json`. Needs a live check with Premium. |
| Switching in game | ⏳ The mode pill, the Settings radio and the keybind should re-decide the current location within about a second. |

## Radio and music folders (added 2026-11-22)
| Item | Result |
|---|---|
| Why | Spotify Development Mode apps allow the owner plus 5 allow-listed users (Feb 2026 rules), so a shared client ID can't serve the public. Radio and folders give everyone music with no account. |
| Sources | `MusicUri`: `spotify:…`, `radio:<url>` (any http(s) URL except Spotify/YouTube) and `folder:<path>` (drive or UNC path). Accepted in game (`M.ParseMusicLink`), in the Zone playlists window and in `config.json`. YouTube is rejected with a reason. |
| Player | `LocalPlayer` (NAudio): a decode thread per track fills an 8 s buffer, so a slow stream never stalls the device; output is WASAPI shared at 48 kHz stereo with volume and ducking ramps. Media Foundation handles MP3/AAC/M4A/WMA/WAV/FLAC and HTTP streams; NAudio.Vorbis handles Ogg. Redirects are followed by hand, since .NET won't follow https→http and Media Foundation fails on some. `.pls`/`.m3u` work by extension or content type. A dropped stream is retried 3 times. |
| Verified | ✅ Unit tests: a mono 44.1 kHz WAV decodes to 48 kHz stereo at the right level, the addon's own Ogg chirps decode, folder listing, playlist parsing, link parsing, radio defaults by `defaultSource`, and folder resume. ✅ All 11 built-in stations stream and decode live (`AUDIOVIBES_NET_TESTS=1`). |
| Not verified | ⏳ Hearing it: nothing was played through speakers from here. Check that a zone change switches stations, that ducking and volume keys work on radio, that Spotify pauses when radio takes over, and the reverse. |
| Routing | While radio or folder music is loaded, play/pause/next/prev and volume go to the companion's player instead of Spotify. Ducking applies to both. Like says it only works with Spotify. |
| Correction | Earlier I said play/pause would work on a YouTube Music tab through Windows' media controls. It wouldn't: `SpotifyTransport` only targets Spotify's media session. |

## Source-aware addon and more stations (added 2026-11-22)
| Item | Result |
|---|---|
| Why | Next did nothing on radio, and the companion's "no next track" notice is a Windows balloon, which is mostly hidden while a game runs full screen. Radio Rivendell was the Relaxed default for most outdoor vibes, the Barrens included. |
| Companion → addon | `Resolved.lua` now also has `source = { default, spotify }`, rewritten when Spotify or settings change (WoW reads it on login and `/reload`). |
| Addon | `M.CurrentSource()` works out what plays: the location's playlist, else the companion's default source, or Hype or a slot until the next location. Unknown when a change is pending a reload. On radio, Next, Previous and Like are greyed out on both bars, and keybinds and `/vibes` print why. Like is also off for folders and when the companion has no Spotify. The bar, "You are here" and Settings › Music show the source. If the companion's setup is unknown (an older companion), nothing is greyed out. |
| Stations | 25 stations (was 11), with Radio Caprice genre channels (Native American, Siberia & Mongolia, Middle Eastern, African folk, medieval, dungeon synth, dark ambient, folk and epic metal), StreamingSoundtracks, CeltCast and Radio Art Didgeridoo. Each has a `backup`, which `ZoneMusic.PlayAsync` plays when the station's stream fails to start; Radio Caprice uses bare IP addresses that could change. Undercity moved to the spooky vibe. |
| Verified | ✅ All 25 stations stream and decode live through `LocalPlayer`. ✅ Backup fallback (unit test). ✅ The addon greys out controls on radio and the UI runs without errors (Lua tests). |
| Not verified | ⏳ Whether each station sounds right for its zones: picked from genre listings, not by listening. ⏳ How the greyed-out buttons look in game. |

## Settings window and addon installer (added 2026-11-22)
| Item | Result |
|---|---|
| Settings window | ✅ Tabs: General, WoW addon, Music, Spotify, Game events, Advanced. Saving writes config.json and applies it at once, keeping keys the window doesn't show. A smoke test opens it, presses Apply and checks the file. |
| Addon bundled with the companion | ✅ `addon\AudioVibes` is copied next to the exe on build and publish, without `Resolved.lua`. The release script stamps its `## Version`. |
| Finding WoW | ✅ Blizzard's registry `InstallPath`, `World of Warcraft` under the usual folders on every fixed drive, and the configured folder. Each `_flavor_` folder with Interface, WTF or Wow*.exe counts as a client. On this PC it found Forever, Anniversary, Beta, Classic, Classic Era and more. |
| Installing | ✅ Copies to `AudioVibes.installing`, swaps it in, and removes the old copy (or retries next time if WoW had a file open). Keeps the player's `Resolved.lua`. Never touches a junction or symlink, so your dev link is safe; it shows as "Linked to a development folder". Tested with a fake WoW tree and a real junction. |
| Auto-update | On start, any client with an *older* `## Version` is updated, with a balloon "restart WoW". The same version with different files is shown as "different files" and only replaced when the player clicks. If the addon isn't installed anywhere, a balloon offers to open the tab. |
| Not verified | ⏳ Installing into a real `Program Files (x86)` WoW folder. Battle.net usually lets normal users write there; if not, the error says to run the companion as administrator once. |

## Releases and auto-update (added 2026-11-22)
| Item | Result |
|---|---|
| Installer | ✅ `scripts/release.ps1 -Version 1.2.0` builds `WoWAudioVibes-win-Setup.exe` (self-contained .NET 10), the full `.nupkg`, `releases.win.json`, a portable zip and the addon zip. Velopack 1.2.161, pinned as a local dotnet tool. Not yet installed or run on a clean machine. |
| Auto-update | ⏳ `Updater` checks `GithubSource(RepoUrl)` 45 s after start and then every 6 h, downloads in the background, and applies on Quit (`WaitExitThenApplyUpdates`) or on *Restart to update*. Velopack also applies a downloaded update on the next start. Untested until two releases exist on GitHub. |
| Release workflow | ⏳ `.github/workflows/release.yml` (tag `v*`) hasn't run yet. |
| Size | The self-contained runtime plus the Windows SDK projection is most of the size: on .NET 10, Setup.exe is 65 MB and the full package 58 MB (on .NET 8 they were 83 and 75 MB). Updates after the first are delta packages. |
| .NET 10 | ✅ Moved from .NET 8 (support ended 10 Nov 2026) to .NET 10 LTS (supported to Nov 2028). SDK pinned in `global.json`. Packages updated: ProtectedData 10.0.15, Serilog 4.4.0, Serilog.Sinks.File 7.0.0, Test.Sdk 18.12.0, xunit runner 3.1.5. Builds with no warnings; all tests pass. |
| Not code-signed | SmartScreen warns on first run. `vpk pack` takes `--signParams` or `--azureTrustedSignFile` if you get a certificate. |

## Gotchas found
- **Long paths**: .NET apphost `.exe`s misbehave when the repo lives in a very deep folder (> 260 chars). The repo now lives at `C:\Users\charl\projects\wow-audio-vibes`, where the `.exe`s work. If you move it, keep the path short.
- **Tests and local state**: the companion tests set `AUDIOVIBES_HOME` to a temp folder. Before that change they read the real `%APPDATA%\WoW Audio Vibes\zone-state.json` and failed once real playlists were cached.
- The Spotify OAuth callback uses a raw `TcpListener`, because `HttpListener` on `127.0.0.1` needs an admin URL ACL.

## Deviations from the spec
- **Checksum swap detection**: the spec formula can't catch S0/S1 swaps that differ by 4 (see PROTOCOL.md). I kept the formula and documented the limitation.
- **Combat trigger default off.** The events are only "entered/left combat", with no combat data, but chirping on every pull is noisy. Turn it on with `/vibes trigger combat`.
- **UI glyphs**: WoW's default fonts have no ⏮⏯⏭♥, so the bar uses `|<  >||  >|  -  +  Like  Slot`.
- **Playlist dropdown** is a small custom popup instead of `UIDropDownMenu`, which is deprecated or unstable across clients.
- An extra **`AudioVibes.Cli`** console project covers milestones 3 and 4 (levels / decode / record / offline file decode).
