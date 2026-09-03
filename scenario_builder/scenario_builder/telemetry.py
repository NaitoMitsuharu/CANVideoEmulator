"""Aligned, compact GNSS + IMU telemetry for the player's HUD overlay.

The scenario timeline has t=0 at the first CAN frame -- the same base the CAN
timeline and ``video_can_offset_ms`` use.  Every sample written here is placed
on that timeline (``t`` in seconds from the first CAN frame) so the player can
ask "what was happening 3.2 s in" without knowing anything about the device's
boot clock.

Two deliberate reductions keep the file small and the graphs cheap to draw:

* GNSS fixes are kept as-is (a few hundred per minute) and also projected to
  local east/north metres from a reference fix, so the player can plot the
  trajectory with no trigonometry of its own.
* IMU streams are decimated to :data:`DEFAULT_IMU_HZ`.  The overlay graphs are a
  hundred-odd pixels wide over a 10 s window, so full sensor rate would be far
  more resolution than can be shown.  Magnetometer values are converted from
  tesla to microtesla so the numbers read naturally.
"""

from __future__ import annotations

import json
from math import cos, radians
from pathlib import Path

import numpy as np

from . import comma2k19

TELEMETRY_FORMAT_VERSION = 1

DEFAULT_IMU_HZ = 25.0
MPS_TO_KMH = 3.6
TESLA_TO_MICROTESLA = 1e6
# WGS84 mean metres per degree of latitude; longitude is scaled by cos(lat).
METRES_PER_DEG_LAT = 111_320.0

# Per-series scaling and rounding for the IMU document.
_IMU_UNITS = {
    "accelerometer": ("m/s^2", 1.0, 4),
    "gyro": ("rad/s", 1.0, 5),
    "magnetometer": ("uT", TESLA_TO_MICROTESLA, 3),
}


def _decimate_indices(t: np.ndarray, target_hz: float) -> np.ndarray:
    """Indices that thin ``t`` to about ``target_hz``, keeping the endpoints."""
    if t.size <= 2 or target_hz <= 0:
        return np.arange(t.size)
    span = float(t[-1] - t[0])
    if span <= 0:
        return np.arange(t.size)
    max_points = int(span * target_hz) + 1
    if max_points >= t.size:
        return np.arange(t.size)
    return np.unique(np.linspace(0, t.size - 1, max_points).round().astype(int))


def _round_list(values: np.ndarray, decimals: int) -> list[float]:
    return [round(float(v), decimals) for v in values]


def build_telemetry(segment: comma2k19.Segment, first_frame_mono_ns: int, *,
                    imu_hz: float = DEFAULT_IMU_HZ,
                    warnings: list[str] | None = None) -> dict | None:
    """Assemble the telemetry document, or ``None`` when nothing is available.

    ``first_frame_mono_ns`` is the boot-monotonic timestamp of the first CAN
    frame (``RawLogCan.first_frame_mono_ns``); it defines t=0 for the scenario.
    """
    warn = warnings if warnings is not None else []
    t0 = first_frame_mono_ns / 1e9
    document: dict = {"format_version": TELEMETRY_FORMAT_VERSION}
    have_any = False

    gnss = comma2k19.read_live_gnss(segment)
    if gnss is not None and gnss.t.size:
        ref_lat = float(np.median(gnss.lat))
        ref_lon = float(np.median(gnss.lon))
        east = (gnss.lon - ref_lon) * (METRES_PER_DEG_LAT * cos(radians(ref_lat)))
        north = (gnss.lat - ref_lat) * METRES_PER_DEG_LAT
        document["gnss"] = {
            "source": gnss.source,
            "ref_lat": round(ref_lat, 7),
            "ref_lon": round(ref_lon, 7),
            "t": _round_list(gnss.t - t0, 3),
            "speed_kmh": _round_list(gnss.speed_mps * MPS_TO_KMH, 2),
            "lat": _round_list(gnss.lat, 7),
            "lon": _round_list(gnss.lon, 7),
            "east_m": _round_list(east, 2),
            "north_m": _round_list(north, 2),
        }
        have_any = True
    else:
        warn.append("processed_log/GNSS/live_gnss_* is missing or unreadable; "
                    "the map and speed overlay will be empty for this scenario.")

    imu = comma2k19.read_imu(segment)
    imu_document: dict = {}
    for name, sig in imu.items():
        t = np.asarray(sig.t, dtype=np.float64)
        value = np.asarray(sig.value, dtype=np.float64)
        if value.ndim != 2 or value.shape[1] < 3 or t.size != value.shape[0]:
            continue
        idx = _decimate_indices(t, imu_hz)
        unit, scale, decimals = _IMU_UNITS[name]
        imu_document[name] = {
            "unit": unit,
            "t": _round_list(t[idx] - t0, 3),
            "x": _round_list(value[idx, 0] * scale, decimals),
            "y": _round_list(value[idx, 1] * scale, decimals),
            "z": _round_list(value[idx, 2] * scale, decimals),
        }
    if imu_document:
        document["imu"] = imu_document
        have_any = True
    else:
        warn.append("processed_log/IMU is missing or unreadable; the IMU graphs "
                    "will be empty for this scenario.")

    return document if have_any else None


def ensure_for_package(package_dir: Path, segment: comma2k19.Segment) -> bool:
    """Add ``telemetry.json`` to an already-converted package that lacks it.

    Lets a re-run of the converter top up telemetry for packages built before it
    existed, without re-encoding the video.  t=0 is reconstructed from the video
    offset the package already recorded -- ``video_can_offset_ms =
    (frame_times[0] - t0) * 1000`` -- so ``raw_log.bz2`` need not be parsed again.

    Returns True when a new ``telemetry.json`` was written; False when the package
    already had one, the segment carried no GNSS/IMU, or t=0 could not be
    reconstructed (no ``frame_times``).
    """
    manifest_path = package_dir / "scenario.json"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return False

    name = manifest.get("telemetry")
    if name and (package_dir / name).is_file():
        return False

    frame_times = comma2k19.read_frame_times(segment)
    if frame_times is None or frame_times.size == 0:
        return False
    offset_ms = float(manifest.get("video_can_offset_ms", 0.0))
    first_frame_mono_ns = int(round((float(frame_times[0]) - offset_ms / 1000.0) * 1e9))

    document = build_telemetry(segment, first_frame_mono_ns)
    if document is None:
        return False

    (package_dir / "telemetry.json").write_text(
        json.dumps(document, ensure_ascii=False) + "\n", encoding="utf-8")
    manifest["telemetry"] = "telemetry.json"
    manifest_path.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return True
