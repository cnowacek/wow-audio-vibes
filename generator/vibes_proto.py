"""Shared protocol helpers: load protocol.json, render frames, and a Python port of
the companion's decoder (same algorithm as companion/src/AudioVibes.Decoder).

Keep this file and FrameDecoder.cs in lock-step. If you change the detection
logic in one, change it in the other.

Two frame kinds share the data band and timing but have different preamble chords:
  command frame: PRE_CMD  · S0 · S1 · CHK          (6-bit command code)
  zone frame:    PRE_ZONE · Z0 · Z1 · Z2 · CHK     (9 bits = part<<8 | byte; two parts make a 16-bit zone id)
"""
from __future__ import annotations

import json
import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

REPO_ROOT = Path(__file__).resolve().parent.parent
PROTOCOL_PATH = REPO_ROOT / "protocol" / "protocol.json"

CMD, ZONE = "cmd", "zone"


# --------------------------------------------------------------------------- #
# Protocol
# --------------------------------------------------------------------------- #
@dataclass
class Protocol:
    raw: dict
    sample_rate: int
    base_hz: float
    step_hz: float
    tone_count: int
    preamble_hz: list[float]
    zone_preamble_hz: list[float]
    reference_hz: list[float]
    preamble_ms: float
    symbol_ms: float
    guard_ms: float
    fade_ms: float
    padding_ms: float
    amplitude_dbfs: float
    chk_a: int
    chk_b: int
    chk_c: int
    zone_chk: tuple[int, int, int, int]
    zone_pair_window_ms: float
    instance_flag: int
    decoder: dict
    commands: dict[str, int]

    @property
    def data_hz(self) -> list[float]:
        return [self.base_hz + self.step_hz * k for k in range(self.tone_count)]

    @property
    def code_to_name(self) -> dict[int, str]:
        return {v: k for k, v in self.commands.items()}

    def samples(self, ms: float) -> int:
        return int(round(ms * self.sample_rate / 1000.0))

    def symbol_count(self, kind: str) -> int:
        return 3 if kind == CMD else 4

    def preamble_for(self, kind: str) -> list[float]:
        return self.preamble_hz if kind == CMD else self.zone_preamble_hz

    def frame_samples_for(self, kind: str) -> int:
        """Preamble start to end of the last symbol (no padding)."""
        return self.samples(self.preamble_ms) + self.symbol_count(kind) * (
            self.samples(self.guard_ms) + self.samples(self.symbol_ms))

    @property
    def frame_samples(self) -> int:
        return self.frame_samples_for(CMD)


def load_protocol(path: Path = PROTOCOL_PATH, base_hz: float | None = None,
                  amplitude_dbfs: float | None = None) -> Protocol:
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    a = raw["audio"]
    z = raw["zoneFrame"]
    if base_hz is not None:
        # Shift the whole band, preserving the preamble/reference offsets from the data band.
        shift = base_hz - a["dataTones"]["baseHz"]
        a["dataTones"]["baseHz"] = base_hz
        a["preambleHz"] = [f + shift for f in a["preambleHz"]]
        a["referenceHz"] = [f + shift for f in a["referenceHz"]]
        z["preambleHz"] = [f + shift for f in z["preambleHz"]]
    if amplitude_dbfs is not None:
        a["amplitudeDbfs"] = amplitude_dbfs
    chk = raw["frame"]["checksum"]
    zc = z["checksum"]
    return Protocol(
        raw=raw,
        sample_rate=a["sampleRate"],
        base_hz=a["dataTones"]["baseHz"],
        step_hz=a["dataTones"]["stepHz"],
        tone_count=a["dataTones"]["count"],
        preamble_hz=list(a["preambleHz"]),
        zone_preamble_hz=list(z["preambleHz"]),
        reference_hz=list(a["referenceHz"]),
        preamble_ms=a["preambleMs"],
        symbol_ms=a["symbolMs"],
        guard_ms=a["guardMs"],
        fade_ms=a["fadeMs"],
        padding_ms=a["paddingMs"],
        amplitude_dbfs=a["amplitudeDbfs"],
        chk_a=chk["a"], chk_b=chk["b"], chk_c=chk["c"],
        zone_chk=(zc["a"], zc["b"], zc["c"], zc["d"]),
        zone_pair_window_ms=z["pairWindowMs"],
        instance_flag=z["instanceFlag"],
        decoder=raw["decoder"],
        commands=dict(raw["commands"]),
    )


# --------------------------------------------------------------------------- #
# Payload
# --------------------------------------------------------------------------- #
def code_to_symbols(code: int) -> tuple[int, int]:
    if not 0 <= code < 64:
        raise ValueError(f"code out of range: {code}")
    return code // 8, code % 8


def symbols_to_code(s0: int, s1: int) -> int:
    return s0 * 8 + s1


def checksum(p: Protocol, s0: int, s1: int) -> int:
    return (p.chk_a * s0 + p.chk_b * s1 + p.chk_c) % 8


def zone_value(part: int, byte: int) -> int:
    if part not in (0, 1) or not 0 <= byte < 256:
        raise ValueError(f"bad zone part/byte {part}/{byte}")
    return (part << 8) | byte


def zone_value_to_symbols(v: int) -> tuple[int, int, int]:
    return (v >> 6) & 7, (v >> 3) & 7, v & 7


def zone_checksum(p: Protocol, z0: int, z1: int, z2: int) -> int:
    a, b, c, d = p.zone_chk
    return (a * z0 + b * z1 + c * z2 + d) % 8


def zone_id_to_parts(zone_id: int) -> tuple[int, int]:
    """16-bit zone id -> (high byte, low byte)."""
    if not 0 <= zone_id < 65536:
        raise ValueError(f"zone id out of range: {zone_id}")
    return zone_id >> 8, zone_id & 0xFF


# --------------------------------------------------------------------------- #
# Rendering
# --------------------------------------------------------------------------- #
def db_to_amp(db: float) -> float:
    return 10.0 ** (db / 20.0)


def tone_segment(p: Protocol, freqs: list[float], ms: float) -> np.ndarray:
    n = p.samples(ms)
    t = np.arange(n, dtype=np.float64) / p.sample_rate
    peak = db_to_amp(p.amplitude_dbfs)
    per_tone = peak / len(freqs)  # chord peak never exceeds the target
    sig = np.zeros(n, dtype=np.float64)
    for f in freqs:
        sig += per_tone * np.sin(2.0 * math.pi * f * t)
    nf = p.samples(p.fade_ms)
    if nf > 0:
        ramp = 0.5 - 0.5 * np.cos(math.pi * np.arange(nf) / nf)
        sig[:nf] *= ramp
        sig[-nf:] *= ramp[::-1]
    return sig


def _render(p: Protocol, preamble: list[float], symbols: list[int], padding: bool) -> np.ndarray:
    data = p.data_hz
    gap = np.zeros(p.samples(p.guard_ms))
    parts = [tone_segment(p, preamble, p.preamble_ms)]
    for s in symbols:
        parts += [gap, tone_segment(p, [data[s]], p.symbol_ms)]
    frame = np.concatenate(parts)
    if padding:
        pad = np.zeros(p.samples(p.padding_ms))
        frame = np.concatenate([pad, frame, pad])
    return frame.astype(np.float32)


def frame_symbols(p: Protocol, code: int) -> list[int]:
    s0, s1 = code_to_symbols(code)
    return [s0, s1, checksum(p, s0, s1)]


def zone_frame_symbols(p: Protocol, part: int, byte: int) -> list[int]:
    z0, z1, z2 = zone_value_to_symbols(zone_value(part, byte))
    return [z0, z1, z2, zone_checksum(p, z0, z1, z2)]


def render_frame(p: Protocol, code: int, padding: bool = True) -> np.ndarray:
    return _render(p, p.preamble_hz, frame_symbols(p, code), padding)


def render_zone_frame(p: Protocol, part: int, byte: int, padding: bool = True) -> np.ndarray:
    return _render(p, p.zone_preamble_hz, zone_frame_symbols(p, part, byte), padding)


# --------------------------------------------------------------------------- #
# Goertzel
# --------------------------------------------------------------------------- #
def goertzel_amp(x: np.ndarray, freq: float, fs: int, window: np.ndarray | None = None) -> float:
    """Amplitude estimate normalised so a full-scale sine reads 1.0."""
    x = np.asarray(x, dtype=np.float64)
    w = np.ones(len(x)) if window is None else window
    xw = x * w
    # Direct DFT at one frequency (equivalent to Goertzel; vectorised for speed).
    n = np.arange(len(xw))
    ph = np.exp(-2j * math.pi * freq * n / fs)
    X = np.dot(xw, ph)
    return float(2.0 * abs(X) / w.sum())


def amp_db(a: float) -> float:
    return 20.0 * math.log10(max(a, 1e-15))


# --------------------------------------------------------------------------- #
# Decoder (Python port of AudioVibes.Decoder.FrameDecoder)
# --------------------------------------------------------------------------- #
@dataclass
class Decoded:
    code: int
    name: str
    confidence_db: float
    sample_index: int
    symbol_levels_db: list[float] = field(default_factory=list)


@dataclass
class ZonePart:
    part: int
    byte: int
    confidence_db: float
    sample_index: int


@dataclass
class ZoneDecoded:
    zone_id: int          # raw 16-bit id
    is_instance: bool
    id: int               # uiMapID or instanceID
    sample_index: int


@dataclass
class Rejected:
    reason: str
    sample_index: int


class FrameDecoder:
    def __init__(self, p: Protocol):
        self.p = p
        d = p.decoder
        self.hop = d["hopSamples"]
        self.win = d["windowSamples"]
        self.hann = 0.5 - 0.5 * np.cos(2 * math.pi * np.arange(self.win) / self.win)
        self.min_pre_db = d["minPreambleDb"]
        self.min_pre_hops = d["minPreambleHops"]
        self.max_pre_hops = d["maxPreambleHops"]
        self.min_margin_db = d["minSymbolMarginDb"]
        self.debounce_samples = int(d["debounceMs"] * p.sample_rate / 1000)
        self.pair_window = int(p.zone_pair_window_ms * p.sample_rate / 1000)
        self.abs_floor = d["absoluteFloorDbfs"]
        self.median_hops = d["noiseMedianHops"]
        self.buf = np.zeros(0, dtype=np.float64)
        self.next_hop_start = 0
        self.ref_hist: list[float] = []
        self.state = "idle"
        self.kind = CMD
        self.pre_first = self.pre_last = 0
        self.pre_count = 0
        self.frame_start = 0
        self.ignore_until = 0
        self.last_key = None
        self.last_key_at = -10**12
        self.zone_hi: tuple[int, int] | None = None   # (byte, sample_index)
        self.events: list = []

    def _lv(self, x, f):
        return amp_db(goertzel_amp(x, f, self.p.sample_rate, self.hann))

    def feed(self, block: np.ndarray) -> list:
        self.buf = np.concatenate([self.buf, np.asarray(block, dtype=np.float64)])
        out_start = len(self.events)
        while self.next_hop_start + self.win <= len(self.buf):
            self._process_hop(self.next_hop_start)
            self.next_hop_start += self.hop
            self._try_finish()
        self._try_finish()
        return self.events[out_start:]

    def _process_hop(self, start: int):
        p = self.p
        x = self.buf[start:start + self.win]
        cmd = [self._lv(x, f) for f in p.preamble_hz]
        zone = [self._lv(x, f) for f in p.zone_preamble_hz]
        ref = [goertzel_amp(x, f, p.sample_rate, self.hann) for f in p.reference_hz]
        ref_db = amp_db(math.sqrt(sum(r * r for r in ref) / len(ref)))
        self.ref_hist.append(ref_db)
        if len(self.ref_hist) > self.median_hops:
            self.ref_hist.pop(0)
        floor = max(float(np.median(self.ref_hist)), ref_db, self.abs_floor)
        thr = floor + self.min_pre_db
        cmd_hit = min(cmd) >= thr
        zone_hit = min(zone) >= thr
        hit_kind = None
        if cmd_hit or zone_hit:
            hit_kind = CMD if (cmd_hit and (not zone_hit or min(cmd) >= min(zone))) else ZONE
        if start < self.ignore_until:
            hit_kind = None

        if self.state == "idle":
            if hit_kind:
                self.state, self.kind = "preamble", hit_kind
                self.pre_first = self.pre_last = start
                self.pre_count = 1
        elif self.state == "preamble":
            if hit_kind == self.kind:
                self.pre_count += 1
                self.pre_last = start
                if self.pre_count > self.max_pre_hops:
                    self.state = "lockout"
                    self.events.append(Rejected("preamble too long", start))
            elif self.pre_count >= self.min_pre_hops:
                self.frame_start = self._sync()
                self.state = "symbols"
            elif hit_kind:  # kind flipped on a too-short preamble: start over with the new kind
                self.kind, self.pre_first, self.pre_last, self.pre_count = hit_kind, start, start, 1
            else:
                self.state = "idle"
        elif self.state == "lockout":
            if not hit_kind:
                self.state = "idle"

    def _sync(self) -> int:
        fs = self.p.sample_rate
        pre_n = self.p.samples(self.p.preamble_ms)
        step = max(1, fs // 1000)
        lo = max(0, self.pre_first - pre_n)
        hi = self.pre_last + self.win
        best, best_c = -1.0, lo
        for c in range(lo, hi + 1, step):
            seg = self.buf[c:c + pre_n]
            if len(seg) < pre_n:
                break
            score = sum(goertzel_amp(seg, f, fs) for f in self.p.preamble_for(self.kind))
            if score > best:
                best, best_c = score, c
        return best_c

    def _try_finish(self):
        if self.state != "symbols":
            return
        p, kind = self.p, self.kind
        end = self.frame_start + p.frame_samples_for(kind)
        if len(self.buf) < end:
            return
        pre_n, g, s = p.samples(p.preamble_ms), p.samples(p.guard_ms), p.samples(p.symbol_ms)
        syms, margins = [], []
        for i in range(p.symbol_count(kind)):
            center = self.frame_start + pre_n + g + i * (g + s) + s // 2
            x = self.buf[center - self.win // 2: center + self.win // 2]
            lv = [self._lv(x, f) for f in p.data_hz]
            order = np.argsort(lv)[::-1]
            syms.append(int(order[0]))
            margins.append(lv[order[0]] - lv[order[1]])
        self.state = "idle"
        self.ignore_until = end
        if min(margins) < self.min_margin_db:
            self.events.append(Rejected(f"symbol margin {min(margins):.1f} dB", self.frame_start))
            return
        if kind == CMD:
            s0, s1, chk = syms
            if checksum(p, s0, s1) != chk:
                self.events.append(Rejected("checksum", self.frame_start))
                return
            code = symbols_to_code(s0, s1)
            if code in p.raw["frame"]["reservedCodes"]:
                self.events.append(Rejected("reserved code", self.frame_start))
                return
            if self._debounced((CMD, code)):
                return
            self.events.append(Decoded(code, p.code_to_name.get(code, f"CODE_{code}"),
                                       float(min(margins)), self.frame_start))
        else:
            z0, z1, z2, chk = syms
            if zone_checksum(p, z0, z1, z2) != chk:
                self.events.append(Rejected("zone checksum", self.frame_start))
                return
            v = (z0 << 6) | (z1 << 3) | z2
            part, byte = v >> 8, v & 0xFF
            if self._debounced((ZONE, v)):
                return
            self.events.append(ZonePart(part, byte, float(min(margins)), self.frame_start))
            if part == 0:
                self.zone_hi = (byte, self.frame_start)
            elif self.zone_hi and self.frame_start - self.zone_hi[1] <= self.pair_window:
                zid = (self.zone_hi[0] << 8) | byte
                self.zone_hi = None
                inst = bool(zid & p.instance_flag)
                self.events.append(ZoneDecoded(zid, inst, zid & (p.instance_flag - 1), self.frame_start))
            else:
                self.zone_hi = None
                self.events.append(Rejected("zone low byte without high byte", self.frame_start))

    def _debounced(self, key) -> bool:
        if key == self.last_key and self.frame_start - self.last_key_at < self.debounce_samples:
            return True
        self.last_key, self.last_key_at = key, self.frame_start
        return False


def decode_all(p: Protocol, signal: np.ndarray, block: int = 1024) -> list:
    dec = FrameDecoder(p)
    out = []
    for i in range(0, len(signal), block):
        out += dec.feed(signal[i:i + block])
    return out
