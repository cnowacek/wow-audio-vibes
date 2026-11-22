# Audio Vibes signal protocol v1

`protocol.json` is the source of truth. This page explains it. If they disagree, the JSON wins.

## Signal

| | |
|---|---|
| Sample rate | 48 kHz mono, float, Ogg Vorbis at quality 1.0 (q10) |
| Data tones | 8-FSK: `f_k = 17000 + 200·k` Hz (17.0–18.4 kHz), 3 bits/symbol |
| Preamble | 16.6 kHz + 18.8 kHz chord, 40 ms (each tone at half the peak amplitude). Zone frames use 16.4 + 19.0 kHz instead (see *Zone frames*). |
| Reference bins | 15.5 kHz and 19.6 kHz. They carry no signal and are used for the noise floor |
| Symbol / guard | 30 ms / 5 ms silence |
| Fades | 5 ms raised cosine on every tone segment |
| Peak level | −45 dBFS |
| File padding | 20 ms silence before and after |

```
| pad 20 | PREAMBLE 40 | g5 | S0 30 | g5 | S1 30 | g5 | CHK 30 | pad 20 |   = 145 ms frame, 185 ms file
```

## Payload

- `code = S0·8 + S1` (0..63). Code 0 is reserved and never sent.
- `CHK = (S0 + 3·S1 + 5) mod 8`.
  - Catches every single-symbol error.
  - Catches an S0/S1 swap **except** when the two symbols differ by exactly 4, because then `2(S1−S0) ≡ 0 mod 8`. That affects 8 of the 56 swappable pairs. A swap needs both symbols to be wrong at once, so it's rare; the 6 dB margin test rejects most of those frames anyway.

## Decoder

- Hop 480 samples (10 ms), Hann window 960 (20 ms), Goertzel at 8 data, 2 preamble and 2 reference frequencies.
- Noise floor = max(running median over 51 hops of the reference-bin RMS level, the current hop's reference level, −130 dBFS).
- A preamble hop is one where **both** preamble tones are ≥ `floor + 12 dB`. A preamble needs ≥ 2 consecutive hops. More than 8 hops is rejected as "too long", since it's a steady tone and not a frame.
- Sync: when the preamble ends, a 40 ms rectangular matched filter steps in 1 ms increments over the candidate range. Its peak is the exact preamble start.
- Each symbol is measured with a 20 ms Hann window centred on the 30 ms symbol. Argmax over the 8 data bins must beat the runner-up by ≥ 6 dB.
- The checksum must match and the code must not be 0. The same code within 500 ms is debounced.

Implementations: `companion/src/AudioVibes.Decoder/FrameDecoder.cs` (C#) and `generator/vibes_proto.py` (Python port used for the generator's round-trip check). Keep them in sync.

## Commands

| Codes | Meaning |
|---|---|
| 0 | reserved |
| 1–8 | PLAY_PAUSE, NEXT, PREV, VOL_UP, VOL_DOWN, DUCK_ON, DUCK_OFF, LIKE_CURRENT |
| 9 | PING |
| 10–41 | PLAYLIST_SLOT_0..31 |
| 42–54 | EVT_LOGIN, EVT_ENTER_CITY, EVT_ENTER_OPEN_WORLD, EVT_ENTER_DUNGEON, EVT_ENTER_RAID, EVT_ENTER_PVP, EVT_QUEUE_POP, EVT_PLAYER_DEAD, EVT_PLAYER_ALIVE, EVT_COMBAT_START, EVT_COMBAT_END, EVT_LEVEL_UP, EVT_HYPE |
| 55 | `PLAY_LOCATION`: sent right after a zone id to mean "play this location's playlist now" (Play buttons, Shift-click Hype) |
| 56–63 | spare (rendered, unused) |

`EVT_*` codes are semantic. The companion's `eventMap` in `config.json` decides what each one does.

## Zone frames

Zone frames give every zone its own playlist without using up command codes.

| | |
|---|---|
| Preamble | **16.4 kHz + 19.0 kHz** chord, 40 ms. It's different from the command chord, so the two can't be confused. |
| Payload | `Z0 Z1 Z2` (9 bits = `part << 8 \| byte`) + `CHK = (Z0 + 3·Z1 + 5·Z2 + 7) mod 8`. This catches every single-symbol error. |
| Frame | `PRE_ZONE · Z0 · Z1 · Z2 · CHK` = 180 ms (files are 220 ms with padding) |
| Zone id | 16 bits: bit 15 set = instance (`GetInstanceInfo()` instanceID), otherwise the zone-level `uiMapID` |
| Transport | Two frames, 250 ms apart: part 0 = high byte, part 1 = low byte. The decoder pairs them if the low byte arrives within 700 ms. A low byte on its own is rejected. |
| Files | `zone_<part>_<byte hex>.ogg`, 512 files |

The addon sends the zone id first and then the category event (`EVT_ENTER_*`). The companion collects both for about 1.2 s and makes one decision.

Zone keys in config are `map:<uiMapID>` and `instance:<instanceID>`, e.g. `map:1429` (Elwynn Forest) and `instance:36` (The Deadmines).

## Changing the band or level

1. Edit `audio` in `protocol.json`.
2. Run `python generator/generate.py`. It re-renders the sounds and `Commands.lua`, and runs the round trip.
3. Rebuild the companion, or drop the new `protocol.json` next to `WoWAudioVibes.exe`. It is read at start-up in preference to the embedded copy.
4. **Restart WoW fully.**
