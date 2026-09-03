"""Cross-check DBC decoding against comma2k19's own processed_log (requirement 54).

The point of this module is to prove that the signal definitions shipped to the
Android app really do recover the vehicle's state from the raw CAN we replay.  It
decodes the recorded frames with the candidate DBC, resamples both series onto
the reference timestamps, and reports RMSE / correlation / max error.

It is also how the RAV4 DBC is *chosen* rather than guessed: ``rank_databases``
scores several candidate DBCs against the same reference and the builder records
the winner and the runners-up in validation.json.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Callable, Iterable, Sequence

import numpy as np

from .canbin import CanFrame
from .dbc import Database


@dataclass(slots=True)
class ComparisonResult:
    """One decoded signal measured against a processed_log reference."""

    reference: str
    source: str                 # "MESSAGE_NAME.SIGNAL_NAME" or an expression label
    unit_reference: str
    unit_decoded: str
    samples: int
    rmse: float
    correlation: float
    max_abs_error: float
    mean_reference: float
    mean_decoded: float
    scale_note: str = ""

    def as_dict(self) -> dict:
        return asdict(self)


@dataclass(slots=True)
class ValidationReport:
    dbc_profile: str
    dbc_files: list[str]
    comparisons: list[ComparisonResult] = field(default_factory=list)
    decodable_message_ids: list[int] = field(default_factory=list)
    undecodable_message_ids: list[int] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    def score(self) -> float:
        """Lower is better.  Mean normalised RMSE over the comparisons made."""
        if not self.comparisons:
            return math.inf
        total = 0.0
        for c in self.comparisons:
            scale = max(abs(c.mean_reference), 1e-6)
            total += c.rmse / scale
        return total / len(self.comparisons)

    def as_dict(self) -> dict:
        return {
            "dbc_profile": self.dbc_profile,
            "dbc_files": self.dbc_files,
            "score": None if math.isinf(self.score()) else round(self.score(), 6),
            "comparisons": [c.as_dict() for c in self.comparisons],
            "decodable_message_id_count": len(self.decodable_message_ids),
            "undecodable_message_id_count": len(self.undecodable_message_ids),
            "undecodable_message_ids": [f"0x{i:X}" for i in self.undecodable_message_ids],
            "notes": self.notes,
        }


# ---------------------------------------------------------------------------
# Reference extractors
#
# processed_log stores SI units (m/s for speed, degrees for the steering angle);
# the Toyota DBC stores km/h with an offset for the wheel speeds.  Each entry
# says how to turn decoded DBC values into the reference's unit, so the numbers
# in validation.json compare like with like.
# ---------------------------------------------------------------------------

KMH_TO_MS = 1.0 / 3.6


@dataclass(slots=True)
class SignalProbe:
    """How to build one comparison series out of decoded CAN."""

    reference: str                                  # processed_log/CAN/<name>
    label: str                                      # human readable source
    message_name: str
    signal_names: Sequence[str]
    unit_decoded: str
    reduce: Callable[[list[float]], float] = lambda values: values[0]
    convert: Callable[[float], float] = lambda value: value
    reference_column: int | None = None
    scale_note: str = ""


TOYOTA_PROBES: list[SignalProbe] = [
    SignalProbe(
        reference="speed",
        label="SPEED.SPEED",
        message_name="SPEED",
        signal_names=("SPEED",),
        unit_decoded="km/h",
        convert=lambda v: v * KMH_TO_MS,
        scale_note="km/h -> m/s",
    ),
    SignalProbe(
        reference="speed",
        label="WHEEL_SPEEDS mean of 4",
        message_name="WHEEL_SPEEDS",
        signal_names=("WHEEL_SPEED_FL", "WHEEL_SPEED_FR",
                      "WHEEL_SPEED_RL", "WHEEL_SPEED_RR"),
        unit_decoded="km/h",
        reduce=lambda values: sum(values) / len(values),
        convert=lambda v: v * KMH_TO_MS,
        scale_note="km/h -> m/s, mean of four wheels",
    ),
    SignalProbe(
        # opendbc's own Toyota CarState builds the reported wheel angle as
        # STEER_ANGLE + STEER_FRACTION (opendbc/car/toyota/carstate.py); the
        # coarse 1.5 deg STEER_ANGLE alone is not the vehicle's angle.
        reference="steering_angle",
        label="STEER_ANGLE_SENSOR.STEER_ANGLE + STEER_FRACTION",
        message_name="STEER_ANGLE_SENSOR",
        signal_names=("STEER_ANGLE", "STEER_FRACTION"),
        unit_decoded="deg",
        reduce=lambda values: values[0] + values[1],
        scale_note="STEER_ANGLE + STEER_FRACTION",
    ),
    SignalProbe(
        reference="wheel_speed",
        label="WHEEL_SPEEDS.WHEEL_SPEED_FL",
        message_name="WHEEL_SPEEDS",
        signal_names=("WHEEL_SPEED_FL",),
        unit_decoded="km/h",
        convert=lambda v: v * KMH_TO_MS,
        reference_column=0,
        scale_note="km/h -> m/s",
    ),
]

HONDA_PROBES: list[SignalProbe] = [
    SignalProbe("speed", "ENGINE_DATA.XMISSION_SPEED", "ENGINE_DATA", ("XMISSION_SPEED",),
                "km/h", convert=lambda v: v * KMH_TO_MS, scale_note="km/h -> m/s"),
    SignalProbe("steering_angle", "STEERING_SENSORS.STEER_ANGLE", "STEERING_SENSORS", ("STEER_ANGLE",), "deg"),
    SignalProbe("wheel_speed", "WHEEL_SPEEDS.WHEEL_SPEED_FL", "WHEEL_SPEEDS", ("WHEEL_SPEED_FL",),
                "km/h", convert=lambda v: v * KMH_TO_MS, reference_column=0, scale_note="km/h -> m/s"),
]

REFERENCE_UNITS = {
    "speed": "m/s",
    "steering_angle": "deg",
    "wheel_speed": "m/s",
}


def _decode_series(frames: Iterable[CanFrame], database: Database,
                   probe: SignalProbe) -> tuple[np.ndarray, np.ndarray]:
    """Decode ``probe`` out of ``frames``, returning (t_seconds, values)."""
    target = None
    for message in database.messages:
        if message.name == probe.message_name:
            target = message
            break
    if target is None:
        return np.empty(0), np.empty(0)

    wanted = set(probe.signal_names)
    times: list[float] = []
    values: list[float] = []
    for frame in frames:
        if frame.can_id != target.frame_id or frame.tx_echo:
            continue
        decoded = target.decode(frame.data)
        if not wanted.issubset(decoded):
            continue
        raw = probe.reduce([decoded[name] for name in probe.signal_names])
        times.append(frame.timestamp_us / 1e6)
        values.append(probe.convert(raw))
    return np.asarray(times), np.asarray(values)


def _reference_series(processed, probe: SignalProbe,
                      t_offset: float) -> tuple[np.ndarray, np.ndarray]:
    signal = processed.get(probe.reference)
    if signal is None:
        return np.empty(0), np.empty(0)
    values = np.asarray(signal.value, dtype=float)
    if values.ndim == 2:
        column = probe.reference_column if probe.reference_column is not None else 0
        if column >= values.shape[1]:
            return np.empty(0), np.empty(0)
        values = values[:, column]
    return np.asarray(signal.t, dtype=float) - t_offset, values


def compare(frames: Sequence[CanFrame], database: Database, processed,
            t_offset: float, probes: Sequence[SignalProbe]) -> list[ComparisonResult]:
    """Produce a ComparisonResult for each probe that has data on both sides."""
    results: list[ComparisonResult] = []
    for probe in probes:
        dec_t, dec_v = _decode_series(frames, database, probe)
        ref_t, ref_v = _reference_series(processed, probe, t_offset)
        if dec_t.size < 8 or ref_t.size < 8:
            continue

        # Compare on the overlapping interval only, sampling the decoded series
        # at the reference timestamps (the reference is the sparser of the two).
        lo = max(dec_t[0], ref_t[0])
        hi = min(dec_t[-1], ref_t[-1])
        mask = (ref_t >= lo) & (ref_t <= hi)
        if mask.sum() < 8:
            continue
        ref_t_c, ref_v_c = ref_t[mask], ref_v[mask]
        dec_v_c = np.interp(ref_t_c, dec_t, dec_v)

        finite = np.isfinite(ref_v_c) & np.isfinite(dec_v_c)
        ref_v_c, dec_v_c = ref_v_c[finite], dec_v_c[finite]
        if ref_v_c.size < 8:
            continue

        error = dec_v_c - ref_v_c
        rmse = float(np.sqrt(np.mean(error ** 2)))
        if np.std(ref_v_c) < 1e-12 or np.std(dec_v_c) < 1e-12:
            correlation = float("nan")
        else:
            correlation = float(np.corrcoef(ref_v_c, dec_v_c)[0, 1])

        results.append(ComparisonResult(
            reference=probe.reference,
            source=probe.label,
            unit_reference=REFERENCE_UNITS.get(probe.reference, ""),
            unit_decoded=probe.unit_decoded,
            samples=int(ref_v_c.size),
            rmse=round(rmse, 6),
            correlation=round(correlation, 6) if not math.isnan(correlation) else None,
            max_abs_error=round(float(np.max(np.abs(error))), 6),
            mean_reference=round(float(np.mean(ref_v_c)), 6),
            mean_decoded=round(float(np.mean(dec_v_c)), 6),
            scale_note=probe.scale_note,
        ))
    return results


def validate(frames: Sequence[CanFrame], database: Database, processed,
             t_offset: float, dbc_profile: str,
             probes: Sequence[SignalProbe] = TOYOTA_PROBES) -> ValidationReport:
    """Full validation of one bus against processed_log."""
    known = set(database.by_frame_id())
    seen: set[int] = set()
    for frame in frames:
        seen.add(frame.can_id)

    report = ValidationReport(
        dbc_profile=dbc_profile,
        dbc_files=list(database.source_files),
        comparisons=compare(frames, database, processed, t_offset, probes),
        decodable_message_ids=sorted(seen & known),
        undecodable_message_ids=sorted(seen - known),
    )
    if not report.comparisons:
        report.notes.append(
            "No reference signal could be compared: either processed_log is absent "
            "or this DBC does not define the messages the probes need.")
    return report


def rank_databases(frames: Sequence[CanFrame], candidates: dict[str, Database],
                   processed, t_offset: float,
                   probes: Sequence[SignalProbe] | None = None) -> list[ValidationReport]:
    """Score candidate DBCs against the same reference; best (lowest score) first."""
    reports = [validate(frames, database, processed, t_offset, name,
                        probes if probes is not None else (HONDA_PROBES if name.startswith("honda_civic") else TOYOTA_PROBES))
               for name, database in candidates.items()]
    reports.sort(key=lambda r: (r.score(), -len(r.decodable_message_ids)))
    return reports
