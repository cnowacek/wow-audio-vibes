import math
import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from vibes_proto import (Decoded, checksum, code_to_symbols, db_to_amp, decode_all,  # noqa: E402
                          load_protocol, render_frame, symbols_to_code)

P = load_protocol()


def test_symbol_code_roundtrip():
    for code in range(64):
        s0, s1 = code_to_symbols(code)
        assert 0 <= s0 < 8 and 0 <= s1 < 8
        assert symbols_to_code(s0, s1) == code


def test_checksum_formula_and_sensitivity():
    for s0 in range(8):
        for s1 in range(8):
            c = checksum(P, s0, s1)
            assert c == (s0 + 3 * s1 + 5) % 8
            # any single-symbol error changes the checksum
            for e in range(1, 8):
                assert checksum(P, (s0 + e) % 8, s1) != c
                assert checksum(P, s0, (s1 + e) % 8) != c
            # swap detection: (s0+3s1) - (s1+3s0) = 2(s1-s0) mod 8, so swaps are caught
            # unless the symbols are equal or differ by exactly 4 (documented in PROTOCOL.md).
            if s0 != s1 and (s1 - s0) % 8 != 4:
                assert checksum(P, s1, s0) != c
            if (s1 - s0) % 8 == 4:
                assert checksum(P, s1, s0) == c


def test_reserved_zero():
    assert 0 in P.raw["frame"]["reservedCodes"]
    assert 0 not in P.commands.values()


def test_frame_length_matches_spec():
    assert P.frame_samples == P.samples(145)
    frame = render_frame(P, 2, padding=True)
    assert len(frame) == P.samples(145 + 2 * P.padding_ms)


def test_peak_amplitude():
    frame = render_frame(P, 2)
    peak_db = 20 * math.log10(np.max(np.abs(frame)))
    assert peak_db <= P.amplitude_dbfs + 0.1
    assert peak_db >= P.amplitude_dbfs - 1.0


def test_fades_no_discontinuities():
    amp = db_to_amp(P.amplitude_dbfs)
    for code in (1, 9, 42, 63):
        x = render_frame(P, code).astype(np.float64)
        # largest sample-to-sample step a clean sine at the highest tone could make
        fmax = max(P.preamble_hz + P.data_hz)
        limit = 2 * math.pi * fmax / P.sample_rate * amp * 1.05
        assert np.max(np.abs(np.diff(x))) <= limit
        # every segment boundary starts/ends near zero
        nz = np.flatnonzero(np.abs(x) > 0)
        assert abs(x[nz[0]]) < 0.05 * amp and abs(x[nz[-1]]) < 0.05 * amp


@pytest.mark.parametrize("code", [c for c in range(1, 64)])
def test_python_decoder_clean(code):
    ev = [e for e in decode_all(P, render_frame(P, code)) if isinstance(e, Decoded)]
    assert [e.code for e in ev] == [code]


def test_zone_checksum_catches_single_symbol_errors():
    from vibes_proto import zone_checksum
    for v in range(512):
        z = [(v >> 6) & 7, (v >> 3) & 7, v & 7]
        c = zone_checksum(P, *z)
        for i in range(3):
            for e in range(1, 8):
                zz = list(z)
                zz[i] = (zz[i] + e) % 8
                assert zone_checksum(P, *zz) != c


def test_zone_frame_length():
    from vibes_proto import ZONE, render_zone_frame
    assert P.frame_samples_for(ZONE) == P.samples(40 + 4 * 35)
    assert len(render_zone_frame(P, 0, 5)) == P.samples(180 + 2 * P.padding_ms)


def _zone_signal(ids, gap_s=0.25, cmd_between=None):
    from vibes_proto import render_zone_frame, zone_id_to_parts
    fs = P.sample_rate
    frames = []
    for zid in ids:
        hi, lo = zone_id_to_parts(zid)
        frames += [render_zone_frame(P, 0, hi, padding=False), render_zone_frame(P, 1, lo, padding=False)]
        if cmd_between:
            frames.append(render_frame(P, cmd_between, padding=False))
    sig = np.zeros(int(fs * (0.3 + gap_s * len(frames) + 0.3)))
    for i, f in enumerate(frames):
        start = int(0.2 * fs) + i * int(gap_s * fs)
        sig[start:start + len(f)] += f
    return sig


def test_zone_ids_decode_and_commands_still_work():
    from vibes_proto import ZoneDecoded
    ids = [1429, 1453, 0x8000 | 36, 2548, 0x8000 | 533]
    ev = decode_all(P, _zone_signal(ids, cmd_between=48), block=999)
    zones = [e for e in ev if isinstance(e, ZoneDecoded)]
    assert [z.zone_id for z in zones] == ids
    assert [z.is_instance for z in zones] == [False, False, True, False, True]
    assert [z.id for z in zones] == [1429, 1453, 36, 2548, 533]
    assert [e.code for e in ev if isinstance(e, Decoded)] == [48] * len(ids)


def test_lone_low_byte_is_not_a_zone():
    from vibes_proto import ZoneDecoded, render_zone_frame
    sig = np.zeros(P.sample_rate)
    f = render_zone_frame(P, 1, 0x95, padding=False)
    sig[4800:4800 + len(f)] += f
    assert not [e for e in decode_all(P, sig) if isinstance(e, ZoneDecoded)]


def test_python_decoder_back_to_back_and_noise():
    rng = np.random.default_rng(1)
    fs = P.sample_rate
    sig = np.zeros(fs * 3)
    codes = [2, 3, 2, 45, 9, 10]
    for i, c in enumerate(codes):
        f = render_frame(P, c, padding=False)
        start = int(0.2 * fs) + i * int(0.25 * fs)
        sig[start:start + len(f)] += f
    sig += rng.normal(0, db_to_amp(-75), len(sig))
    ev = [e.code for e in decode_all(P, sig, block=777) if isinstance(e, Decoded)]
    assert ev == codes
