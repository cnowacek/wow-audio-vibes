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

## Gotchas found
- **Long paths**: .NET apphost `.exe`s misbehave when the repo lives in a very deep folder (> 260 chars). The repo now lives at `C:\Users\charl\AudioVibes`, where the `.exe`s work. If you move it, keep the path short.
- The Spotify OAuth callback uses a raw `TcpListener`, because `HttpListener` on `127.0.0.1` needs an admin URL ACL.

## Deviations from the spec
- **Checksum swap detection**: the spec formula can't catch S0/S1 swaps that differ by 4 (see PROTOCOL.md). I kept the formula and documented the limitation.
- **Combat trigger default off.** The events are only "entered/left combat", with no combat data, but chirping on every pull is noisy. Turn it on with `/vibes trigger combat`.
- **UI glyphs**: WoW's default fonts have no ⏮⏯⏭♥, so the bar uses `|<  >||  >|  -  +  Like  Slot`.
- **Playlist dropdown** is a small custom popup instead of `UIDropDownMenu`, which is deprecated or unstable across clients.
- An extra **`AudioVibes.Cli`** console project covers milestones 3 and 4 (levels / decode / record / offline file decode).
