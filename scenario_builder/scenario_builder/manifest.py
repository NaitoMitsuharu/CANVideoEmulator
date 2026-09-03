"""scenario.json construction, bus statistics and auto-tagging."""

from __future__ import annotations

import statistics
from dataclasses import dataclass, asdict
from typing import Sequence

from .canbin import CanFrame
from .dbc import Database

SCENARIO_FORMAT_VERSION = 1

# Two different bit rates, deliberately never conflated.
#
# original_bitrate is a property of the vehicle whose CAN was recorded.
# comma2k19 publishes no bit rate for the buses it captured and nothing in
# raw_log.bz2 states one, so it stays None.  Writing 500000 here because that is
# the usual Toyota powertrain rate would be inventing a fact about the car.
#
# playback_bitrate is a property of the bench: the rate the PCAN-USB and the
# receiving CAN nodes are configured for.  It is a real, chosen number and belongs in the
# manifest so a package records the conditions it was meant to be played under.
UNKNOWN_ORIGINAL_BITRATE = None
DEFAULT_PLAYBACK_BITRATE = 500_000

# Kept as an alias so older callers keep working.
UNKNOWN_BITRATE = UNKNOWN_ORIGINAL_BITRATE


@dataclass(slots=True)
class BusStatistics:
    """Per-bus figures shown in the player when a bus is selected (req. 29)."""

    bus: int
    frame_count: int
    frames_per_second: float
    unique_can_ids: int
    average_dlc: float
    estimated_bus_load_bps: int
    duration_sec: float
    tx_echo_frame_count: int
    extended_id_frame_count: int
    can_ids: list[str]

    def as_dict(self) -> dict:
        return asdict(self)


def _classical_frame_bits(can_id_extended: bool, dlc: int) -> int:
    """Worst-case bits on the wire for one classical CAN frame.

    Base format: 1 SOF + 11 ID + RTR + IDE + r0 + 4 DLC + 8*dlc data + 15 CRC +
    CRC delim + ACK + ACK delim + 7 EOF + 3 IFS = 47 + 8*dlc.
    Extended format adds the 18-bit ID extension plus SRR and r1 = 20 more bits.
    Stuff bits are not modelled, so this is a floor, and it is reported as an
    *estimate* in the UI.
    """
    return (67 if can_id_extended else 47) + 8 * dlc


def bus_statistics(bus: int, frames: Sequence[CanFrame]) -> BusStatistics:
    if not frames:
        return BusStatistics(bus, 0, 0.0, 0, 0.0, 0, 0.0, 0, 0, [])

    duration = frames[-1].timestamp_us / 1e6
    ids = sorted({f.can_id for f in frames})
    total_dlc = sum(f.dlc for f in frames)
    bits = sum(_classical_frame_bits(f.extended, f.dlc) for f in frames)

    return BusStatistics(
        bus=bus,
        frame_count=len(frames),
        frames_per_second=round(len(frames) / duration, 2) if duration > 0 else 0.0,
        unique_can_ids=len(ids),
        average_dlc=round(total_dlc / len(frames), 3),
        estimated_bus_load_bps=int(bits / duration) if duration > 0 else 0,
        duration_sec=round(duration, 3),
        tx_echo_frame_count=sum(1 for f in frames if f.tx_echo),
        extended_id_frame_count=sum(1 for f in frames if f.extended),
        can_ids=[f"0x{i:X}" for i in ids],
    )


# ---------------------------------------------------------------------------
# Auto tags
#
# Requirement 35: only tag what the data actually shows.  Every rule below is a
# threshold on a quantity decoded from this scenario's own CAN, and the numbers
# behind each tag are written into scenario.json under "tag_evidence" so a tag
# can always be traced back to why it was applied.  Judgements the CAN cannot
# support -- "Traffic", "Curves" as a road classification -- are left for a human
# to add by editing scenario.json.
# ---------------------------------------------------------------------------

MS_TO_KMH = 3.6


def _decode_series(frames: Sequence[CanFrame], database: Database,
                   message_name: str, signal_name: str) -> list[float]:
    target = next((m for m in database.messages if m.name == message_name), None)
    if target is None:
        return []
    out: list[float] = []
    for frame in frames:
        if frame.can_id != target.frame_id or frame.tx_echo:
            continue
        decoded = target.decode(frame.data)
        if signal_name in decoded:
            out.append(decoded[signal_name])
    return out


def auto_tags(frames: Sequence[CanFrame],
              database: Database) -> tuple[list[str], dict]:
    """Derive tags plus the measurements that justify them."""
    tags: list[str] = []
    evidence: dict = {}

    speeds_kmh = _decode_series(frames, database, "WHEEL_SPEEDS", "WHEEL_SPEED_FL")
    if len(speeds_kmh) >= 50:
        median = statistics.median(speeds_kmh)
        low, high = min(speeds_kmh), max(speeds_kmh)
        evidence["speed_kmh"] = {
            "median": round(median, 2), "min": round(low, 2), "max": round(high, 2),
            "samples": len(speeds_kmh), "source": "WHEEL_SPEEDS.WHEEL_SPEED_FL",
        }
        if median >= 80:
            tags.append("High Speed")
        elif median <= 30:
            tags.append("Low Speed")
        if high - low >= 20:
            tags.append("Speed Change")
        if low <= 1.0:
            tags.append("Vehicle Stop")

    angles = _decode_series(frames, database, "STEER_ANGLE_SENSOR", "STEER_ANGLE")
    fractions = _decode_series(frames, database, "STEER_ANGLE_SENSOR", "STEER_FRACTION")
    if len(angles) >= 50:
        if len(fractions) == len(angles):
            angles = [a + f for a, f in zip(angles, fractions)]
        peak = max(abs(a) for a in angles)
        spread = statistics.pstdev(angles)
        evidence["steering_angle_deg"] = {
            "max_abs": round(peak, 2), "stdev": round(spread, 3),
            "samples": len(angles),
            "source": "STEER_ANGLE_SENSOR.STEER_ANGLE + STEER_FRACTION",
        }
        if peak >= 15.0:
            tags.append("Steering Active")
        if spread >= 5.0:
            tags.append("Winding")

    cruise = _decode_series(frames, database, "PCM_CRUISE", "CRUISE_ACTIVE")
    if len(cruise) >= 50:
        share = sum(1 for v in cruise if v >= 0.5) / len(cruise)
        evidence["cruise_active_fraction"] = {
            "value": round(share, 4), "samples": len(cruise),
            "source": "PCM_CRUISE.CRUISE_ACTIVE",
        }
        if share >= 0.5:
            tags.append("Cruise")

    brake = _decode_series(frames, database, "BRAKE_MODULE", "BRAKE_PRESSED")
    if len(brake) >= 50:
        share = sum(1 for v in brake if v >= 0.5) / len(brake)
        evidence["brake_pressed_fraction"] = {
            "value": round(share, 4), "samples": len(brake),
            "source": "BRAKE_MODULE.BRAKE_PRESSED",
        }
        if share >= 0.02:
            tags.append("Braking")

    # GAS_PEDAL.GAS_PEDAL is a percentage in the Toyota DBC ("%", factor 0.5),
    # not a 0..1 fraction.
    gas = _decode_series(frames, database, "GAS_PEDAL", "GAS_PEDAL")
    if len(gas) >= 50:
        peak = max(gas)
        evidence["gas_pedal_max_percent"] = {
            "value": round(peak, 3), "samples": len(gas),
            "unit": "%", "source": "GAS_PEDAL.GAS_PEDAL",
        }
        if peak >= 25.0:
            tags.append("Acceleration")

    # Preserve discovery order but drop duplicates.
    return list(dict.fromkeys(tags)), evidence


def build_scenario_json(*, scenario_id: str, title: str, vehicle_make: str,
                        vehicle_model: str, vehicle_year: int | None,
                        dataset: str, route: str, segment: int,
                        duration_sec: float, video: str, thumbnail: str,
                        available_buses: Sequence[int], default_bus: int,
                        default_bus_reason: str,
                        original_bitrate: int | None,
                        playback_bitrate: int | None, video_can_offset_ms: float,
                        dbc_profile: str, tags: Sequence[str],
                        tag_evidence: dict, description: str,
                        can_files: dict[int, str],
                        bus_stats: Sequence[BusStatistics],
                        video_fps: float, video_frame_count: int | None,
                        source_dongle_id: str) -> dict:
    """Assemble the scenario manifest (requirement 33)."""
    return {
        "format_version": SCENARIO_FORMAT_VERSION,
        "scenario_id": scenario_id,
        "title": title,
        "dataset": dataset,
        "vehicle": f"{vehicle_make} {vehicle_model}".strip(),
        "vehicle_make": vehicle_make,
        "vehicle_model": vehicle_model,
        "vehicle_year": vehicle_year,
        "route": route,
        "segment": segment,
        "source_dongle_id": source_dongle_id,
        "duration_sec": round(duration_sec, 3),
        "video": video,
        "video_fps": video_fps,
        "video_frame_count": video_frame_count,
        "thumbnail": thumbnail,
        "available_buses": list(available_buses),
        "default_bus": default_bus,
        "default_bus_reason": default_bus_reason,
        # Requirement 10 (phase 2): the vehicle's own bus rate and the rate this
        # package is replayed at are different facts and are stored separately.
        # A null original_bitrate means "not documented by the dataset", never
        # "assume the playback rate".
        "original_bitrate": original_bitrate,
        "playback_bitrate": playback_bitrate,
        # Signed offset in milliseconds from CAN t=0 to the first video frame.
        # video_position(t) = t - video_can_offset_ms / 1000.
        "video_can_offset_ms": round(video_can_offset_ms, 3),
        "dbc_profile": dbc_profile,
        "tags": list(tags),
        "tag_evidence": tag_evidence,
        "description": description,
        "can": {str(bus): name for bus, name in sorted(can_files.items())},
        "bus_statistics": [s.as_dict() for s in bus_stats],
    }
