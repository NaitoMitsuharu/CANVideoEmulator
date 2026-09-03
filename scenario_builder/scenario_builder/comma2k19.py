"""Reading comma2k19 segments.

Two facts about this dataset drive the design here, both verified against the
data rather than assumed:

1. ``processed_log/CAN/raw_can/data`` is stored as numpy ``|S8``, which strips
   trailing NUL bytes.  In the repository's own example segment 520 of 135468
   frames decode to length 0, and the DLC is wrong for every frame whose payload
   ends in 0x00.  Requirement 70 forbids altering DLC or DATA, so the CAN
   timeline is always taken from ``raw_log.bz2`` (capnp ``CanData.dat`` is a
   length-prefixed ``Data`` field and is exact).  ``processed_log`` is used only
   for validation reference signals.

2. ``CanData.src`` is the panda's bus byte.  panda firmware
   (board/drivers/can.h, ``#define CAN_BUS_RET_FLAG 0x80U``) pushes frames the
   panda itself transmitted with ``(CAN_BUS_RET_FLAG | bus_number) << 4`` and
   frames it received with ``bus_number << 4``; boardd
   (selfdrive/boardd/panda.cc) stores ``(word >> 4) & 0xff`` into ``src``.  So
   ``src & 0x7F`` is the physical bus and ``src & 0x80`` marks openpilot's own
   transmissions.  Both were physically present on that bus, so both are kept
   and the echoed ones are flagged rather than dropped.
"""

from __future__ import annotations

import bz2
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterator

import numpy as np

from .canbin import CanFrame

_SCHEMA_DIR = Path(__file__).parent / "schema"

CAN_BUS_RET_FLAG = 0x80

# comma2k19 route directories are "<dongle_id>|<utc timestamp>"; "|" is not a
# legal Windows filename character, so segments copied onto Windows are commonly
# renamed with "_".  Accept both spellings.
_ROUTE_RE = re.compile(
    r"^(?P<dongle>[0-9a-f]{16})[|_](?P<time>\d{4}-\d{2}-\d{2}--\d{2}-\d{2}-\d{2})$")

# comma2k19 README: chunks 1-2 are the Toyota RAV4, chunks 3-10 the Honda Civic.
DONGLE_VEHICLES = {
    "b0c9d2329ad1606b": ("Toyota", "RAV4"),
    "99c94dc769b5d96e": ("Honda", "Civic"),
}


def _load_schema():
    import capnp
    capnp.remove_import_hook()
    return capnp.load(str(_SCHEMA_DIR / "log.capnp"))


@dataclass(slots=True)
class Segment:
    """One ~1 minute comma2k19 segment on disk."""

    path: Path
    dongle_id: str
    route_time: str
    segment_index: int

    @property
    def route(self) -> str:
        return f"{self.dongle_id}|{self.route_time}"

    @property
    def video_hevc(self) -> Path:
        return self.path / "video.hevc"

    @property
    def raw_log(self) -> Path:
        return self.path / "raw_log.bz2"

    @property
    def preview(self) -> Path:
        return self.path / "preview.png"

    @property
    def processed_log(self) -> Path:
        return self.path / "processed_log"

    def is_complete(self) -> bool:
        return self.video_hevc.is_file() and self.raw_log.is_file()

    def vehicle(self) -> tuple[str, str]:
        return DONGLE_VEHICLES.get(self.dongle_id, ("Unknown", "Unknown"))


def find_segments(root: Path | str) -> list[Segment]:
    """Discover every segment under a chunk directory, or a single segment dir."""
    root = Path(root)
    found: list[Segment] = []
    seen: set[Path] = set()

    for raw_log in sorted(root.rglob("raw_log.bz2")):
        seg_dir = raw_log.parent
        if seg_dir in seen:
            continue
        seen.add(seg_dir)
        match = _ROUTE_RE.match(seg_dir.parent.name)
        if match is None:
            # Tolerate a bare segment directory handed straight to the builder.
            found.append(Segment(seg_dir, "unknown", "unknown", _int_or(seg_dir.name, 0)))
        else:
            found.append(Segment(seg_dir, match["dongle"], match["time"],
                                 _int_or(seg_dir.name, 0)))

    found.sort(key=lambda s: (s.dongle_id, s.route_time, s.segment_index))
    return found


def _int_or(text: str, default: int) -> int:
    try:
        return int(text)
    except ValueError:
        return default


@dataclass(slots=True)
class RawLogCan:
    """CAN frames from raw_log.bz2, grouped by physical bus."""

    buses: dict[int, list[CanFrame]] = field(default_factory=dict)
    duration_us: int = 0
    first_frame_mono_ns: int = 0
    log_mono_start_ns: int = 0


def read_raw_log_can(raw_log: Path, *, include_tx_echo: bool = True) -> RawLogCan:
    """Decode ``raw_log.bz2`` into per-bus, zero-based CAN timelines.

    Timestamps come from the enclosing ``Event.logMonoTime`` (nanoseconds since
    device boot).  Every frame inside one ``can`` event shares that event's
    timestamp, which is how the panda delivers them; the 16-bit ``busTime`` on
    each frame is a wrapping panda-side counter and is not a usable wall clock.
    """
    log = _load_schema()
    with bz2.open(raw_log, "rb") as handle:
        payload = handle.read()

    buses: dict[int, list[CanFrame]] = {}
    first_mono: int | None = None
    log_start: int | None = None

    for event in log.Event.read_multiple_bytes(payload):
        if log_start is None:
            log_start = int(event.logMonoTime)
        if event.which() != "can":
            continue
        mono = int(event.logMonoTime)
        if first_mono is None:
            first_mono = mono
        rel_us = (mono - first_mono) // 1000
        for frame in event.can:
            src = int(frame.src)
            bus = src & 0x7F
            data = bytes(frame.dat)
            if len(data) > 8:
                # Classical CAN only (requirement 25).  Never truncate: refuse,
                # so a mismatch is visible instead of silently faked.
                raise ValueError(
                    f"frame 0x{int(frame.address):X} on bus {bus} carries "
                    f"{len(data)} data bytes; this builder produces classical CAN "
                    "only and will not truncate")
            tx_echo = bool(src & CAN_BUS_RET_FLAG)
            if tx_echo and not include_tx_echo:
                continue
            address = int(frame.address)
            buses.setdefault(bus, []).append(CanFrame(
                timestamp_us=rel_us,
                can_id=address,
                dlc=len(data),
                data=data,
                extended=address > 0x7FF,
                tx_echo=tx_echo,
            ))

    duration_us = max((frames[-1].timestamp_us for frames in buses.values() if frames),
                      default=0)
    return RawLogCan(
        buses=buses,
        duration_us=duration_us,
        first_frame_mono_ns=first_mono or 0,
        log_mono_start_ns=log_start or 0,
    )


def read_frame_times(segment: Segment) -> np.ndarray | None:
    """Camera frame timestamps (``global_pose/frame_times``), boot-monotonic seconds."""
    path = segment.path / "global_pose" / "frame_times"
    if not path.is_file():
        return None
    return np.load(path, allow_pickle=False)


@dataclass(slots=True)
class ProcessedSignal:
    name: str
    t: np.ndarray
    value: np.ndarray


def read_processed_can(segment: Segment) -> dict[str, ProcessedSignal]:
    """Load ``processed_log/CAN/*`` reference signals used for validation."""
    base = segment.processed_log / "CAN"
    out: dict[str, ProcessedSignal] = {}
    if not base.is_dir():
        return out
    for sub in sorted(base.iterdir()):
        if sub.name == "raw_can" or not sub.is_dir():
            continue
        t_path, value_path = sub / "t", sub / "value"
        if not (t_path.is_file() and value_path.is_file()):
            continue
        out[sub.name] = ProcessedSignal(sub.name,
                                        np.load(t_path, allow_pickle=False),
                                        np.load(value_path, allow_pickle=False))
    return out


def _load_series(base: Path) -> ProcessedSignal | None:
    """Load a comma2k19 ``t``/``value`` numpy pair from a folder, if present."""
    t_path, value_path = base / "t", base / "value"
    if not (t_path.is_file() and value_path.is_file()):
        return None
    return ProcessedSignal(base.name,
                           np.load(t_path, allow_pickle=False),
                           np.load(value_path, allow_pickle=False))


# Calibrated IMU series only; the uncalibrated/bias variants beside them are not
# used for the overlay.  Each ``value`` is Nx3 in the device frame
# [forward, right, down] (comma2k19 README): accelerometer in m/s^2, gyro in
# rad/s, magnetometer in tesla.
IMU_SERIES = ("accelerometer", "gyro", "magnetometer")


def read_imu(segment: Segment) -> dict[str, ProcessedSignal]:
    """Load ``processed_log/IMU/{accelerometer,gyro,magnetometer}`` (boot time)."""
    base = segment.processed_log / "IMU"
    out: dict[str, ProcessedSignal] = {}
    if not base.is_dir():
        return out
    for name in IMU_SERIES:
        sig = _load_series(base / name)
        if sig is not None:
            out[name] = sig
    return out


@dataclass(slots=True)
class GnssTrack:
    """Live GNSS fixes, boot-monotonic ``t`` in seconds."""

    t: np.ndarray          # boot time (s)
    lat: np.ndarray        # degrees
    lon: np.ndarray        # degrees
    speed_mps: np.ndarray  # metres per second
    source: str            # which live_gnss folder it came from


def read_live_gnss(segment: Segment) -> GnssTrack | None:
    """Load ``processed_log/GNSS/live_gnss_*`` (ublox preferred, qcom fallback).

    comma2k19 stores each fix as ``[latitude (deg), longitude (deg),
    speed (m/s), utc_timestamp (s), altitude (m), bearing (deg)]``.  Only the
    first three columns are needed for the map trajectory and speed readout.
    """
    base = segment.processed_log / "GNSS"
    for name in ("live_gnss_ublox", "live_gnss_qcom"):
        sig = _load_series(base / name)
        if sig is None:
            continue
        value = np.asarray(sig.value, dtype=np.float64)
        t = np.asarray(sig.t, dtype=np.float64)
        if value.ndim != 2 or value.shape[1] < 3 or t.size != value.shape[0]:
            continue
        return GnssTrack(t=t, lat=value[:, 0], lon=value[:, 1],
                         speed_mps=value[:, 2], source=name)
    return None


def read_raw_can_reference_times(segment: Segment) -> np.ndarray | None:
    """``processed_log/CAN/raw_can/t`` -- boot-monotonic seconds per raw frame.

    Used only to align the processed-log time base with the raw-log time base.
    The payload arrays beside it are lossy (see the module docstring) and are
    never used for the replay timeline.
    """
    path = segment.processed_log / "CAN" / "raw_can" / "t"
    if not path.is_file():
        return None
    return np.load(path, allow_pickle=False)


def iter_can_events(raw_log: Path) -> Iterator[tuple[int, int, int, bytes]]:
    """Low-level helper yielding ``(logMonoTime_ns, src, address, data)``."""
    log = _load_schema()
    with bz2.open(raw_log, "rb") as handle:
        payload = handle.read()
    for event in log.Event.read_multiple_bytes(payload):
        if event.which() != "can":
            continue
        mono = int(event.logMonoTime)
        for frame in event.can:
            yield mono, int(frame.src), int(frame.address), bytes(frame.dat)
