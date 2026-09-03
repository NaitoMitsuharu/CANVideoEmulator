"""Survey a comma2k19 chunk and recommend segments worth exhibiting.

Chunk 1 alone holds roughly 200 one-minute segments, and converting all of them
would take hours and tens of gigabytes for a stand that shows maybe twenty. This
module reads each segment's CAN once -- without transcoding any video -- measures
what is actually in it, and proposes a varied shortlist.

Analysis is deliberately separate from building: measuring is cheap and
repeatable, transcoding is not, so the expensive step only runs on segments a
human has seen the numbers for.
"""

from __future__ import annotations

import json
import math
import statistics
from dataclasses import dataclass, asdict, field
from pathlib import Path
from typing import Callable, Iterable, Sequence

from . import comma2k19
from .canbin import CanFrame
from .dbc import Database

ANALYSIS_FORMAT_VERSION = 1


@dataclass(slots=True)
class BusSummary:
    bus: int
    frame_count: int
    frames_per_second: float
    unique_can_ids: int
    decodable_can_ids: int
    tx_echo_frame_count: int


@dataclass(slots=True)
class SegmentAnalysis:
    """Everything measured for one segment, with no interpretation applied."""

    segment_path: str
    route: str
    segment_index: int
    dongle_id: str
    vehicle_make: str
    vehicle_model: str

    duration_sec: float
    total_frame_count: int
    frames_per_second: float
    unique_can_ids: int
    buses: list[BusSummary] = field(default_factory=list)
    default_bus: int = 0

    # Decoded from the default bus. None when the signal is absent.
    average_speed_kmh: float | None = None
    min_speed_kmh: float | None = None
    max_speed_kmh: float | None = None
    speed_stdev_kmh: float | None = None
    speed_range_kmh: float | None = None
    speed_sample_count: int = 0

    steering_min_deg: float | None = None
    steering_max_deg: float | None = None
    steering_range_deg: float | None = None
    steering_stdev_deg: float | None = None
    steering_max_abs_deg: float | None = None
    steering_sample_count: int = 0

    cruise_active_fraction: float | None = None
    brake_pressed_fraction: float | None = None
    gas_pedal_max_percent: float | None = None

    has_thumbnail: bool = False
    has_video: bool = False
    has_processed_log: bool = False
    validation_available: bool = False

    error: str | None = None

    def as_dict(self) -> dict:
        document = asdict(self)
        document["buses"] = [asdict(b) if not isinstance(b, dict) else b
                             for b in self.buses]
        return document

    @property
    def is_usable(self) -> bool:
        return self.error is None and self.total_frame_count > 0


# ---------------------------------------------------------------------------
# Measurement
# ---------------------------------------------------------------------------

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


def _fraction_active(values: Sequence[float]) -> float | None:
    if len(values) < 50:
        return None
    return round(sum(1 for v in values if v >= 0.5) / len(values), 4)


def analyze_segment(segment: comma2k19.Segment, database: Database) -> SegmentAnalysis:
    """Read one segment's CAN and measure it. Never raises; errors are recorded."""
    make, model = segment.vehicle()
    analysis = SegmentAnalysis(
        segment_path=str(segment.path),
        route=segment.route,
        segment_index=segment.segment_index,
        dongle_id=segment.dongle_id,
        vehicle_make=make,
        vehicle_model=model,
        duration_sec=0.0,
        total_frame_count=0,
        frames_per_second=0.0,
        unique_can_ids=0,
        has_thumbnail=segment.preview.is_file(),
        has_video=segment.video_hevc.is_file(),
        has_processed_log=(segment.processed_log / "CAN").is_dir(),
    )

    try:
        raw = comma2k19.read_raw_log_can(segment.raw_log)
    except Exception as error:                       # keep scanning the chunk
        analysis.error = f"{type(error).__name__}: {error}"
        return analysis

    if not raw.buses:
        analysis.error = "no CAN frames"
        return analysis

    known = set(database.by_frame_id())
    all_ids: set[int] = set()
    for bus in sorted(raw.buses):
        frames = raw.buses[bus]
        ids = {f.can_id for f in frames}
        all_ids |= ids
        span = frames[-1].timestamp_us / 1e6 if frames else 0.0
        analysis.buses.append(BusSummary(
            bus=bus,
            frame_count=len(frames),
            frames_per_second=round(len(frames) / span, 2) if span > 0 else 0.0,
            unique_can_ids=len(ids),
            decodable_can_ids=len(ids & known),
            tx_echo_frame_count=sum(1 for f in frames if f.tx_echo),
        ))

    # Same rule the builder uses, so the analysis describes the bus that would
    # actually be transmitted.
    best = max(analysis.buses, key=lambda b: (b.decodable_can_ids, b.frame_count))
    analysis.default_bus = best.bus

    analysis.duration_sec = round(raw.duration_us / 1e6, 3)
    analysis.total_frame_count = sum(b.frame_count for b in analysis.buses)
    analysis.frames_per_second = round(
        analysis.total_frame_count / analysis.duration_sec, 2
    ) if analysis.duration_sec > 0 else 0.0
    analysis.unique_can_ids = len(all_ids)

    frames = raw.buses[analysis.default_bus]

    speeds = _decode_series(frames, database, "WHEEL_SPEEDS", "WHEEL_SPEED_FL")
    if len(speeds) >= 50:
        analysis.speed_sample_count = len(speeds)
        analysis.average_speed_kmh = round(statistics.fmean(speeds), 3)
        analysis.min_speed_kmh = round(min(speeds), 3)
        analysis.max_speed_kmh = round(max(speeds), 3)
        analysis.speed_stdev_kmh = round(statistics.pstdev(speeds), 3)
        analysis.speed_range_kmh = round(max(speeds) - min(speeds), 3)

    angles = _decode_series(frames, database, "STEER_ANGLE_SENSOR", "STEER_ANGLE")
    fractions = _decode_series(frames, database, "STEER_ANGLE_SENSOR", "STEER_FRACTION")
    if len(angles) >= 50:
        # opendbc's own Toyota CarState reports STEER_ANGLE + STEER_FRACTION.
        if len(fractions) == len(angles):
            angles = [a + f for a, f in zip(angles, fractions)]
        analysis.steering_sample_count = len(angles)
        analysis.steering_min_deg = round(min(angles), 3)
        analysis.steering_max_deg = round(max(angles), 3)
        analysis.steering_range_deg = round(max(angles) - min(angles), 3)
        analysis.steering_stdev_deg = round(statistics.pstdev(angles), 3)
        analysis.steering_max_abs_deg = round(max(abs(a) for a in angles), 3)

    analysis.cruise_active_fraction = _fraction_active(
        _decode_series(frames, database, "PCM_CRUISE", "CRUISE_ACTIVE"))
    analysis.brake_pressed_fraction = _fraction_active(
        _decode_series(frames, database, "BRAKE_MODULE", "BRAKE_PRESSED"))

    gas = _decode_series(frames, database, "GAS_PEDAL", "GAS_PEDAL")
    if len(gas) >= 50:
        analysis.gas_pedal_max_percent = round(max(gas), 3)

    # Validation needs a processed_log reference *and* a decodable speed signal.
    analysis.validation_available = (
        analysis.has_processed_log and analysis.speed_sample_count > 0)

    return analysis


def analyze_chunk(root: Path, database: Database, *, limit: int = 0,
                  progress: Callable[[str], None] = lambda _: None
                  ) -> list[SegmentAnalysis]:
    segments = [s for s in comma2k19.find_segments(root) if s.is_complete()]
    if limit:
        segments = segments[:limit]

    results: list[SegmentAnalysis] = []
    for index, segment in enumerate(segments, start=1):
        progress(f"[{index}/{len(segments)}] {segment.route} seg {segment.segment_index}")
        results.append(analyze_segment(segment, database))
    return results


# ---------------------------------------------------------------------------
# Recommendation
#
# Each category is a threshold on a measured quantity, so a recommendation can
# always be traced to the numbers that produced it.  Nothing here infers road
# type or traffic: the CAN does not carry that, and requirement 35's rule
# against unjustified tags applies just as much to a shortlist.
# ---------------------------------------------------------------------------

@dataclass(slots=True)
class Category:
    name: str
    description: str
    matches: Callable[[SegmentAnalysis], bool]
    """Higher is a better example of this category."""
    score: Callable[[SegmentAnalysis], float]


def _speed(a: SegmentAnalysis) -> float:
    return a.average_speed_kmh or 0.0


CATEGORIES: list[Category] = [
    Category(
        "High Speed",
        "Average wheel speed at or above 80 km/h.",
        lambda a: (a.average_speed_kmh or 0) >= 80,
        _speed,
    ),
    Category(
        "Cruise",
        "PCM_CRUISE reports cruise active for at least half the segment.",
        lambda a: (a.cruise_active_fraction or 0) >= 0.5,
        lambda a: a.cruise_active_fraction or 0.0,
    ),
    Category(
        "Speed Changes",
        "Wheel speed spans at least 25 km/h within the segment.",
        lambda a: (a.speed_range_kmh or 0) >= 25,
        lambda a: a.speed_range_kmh or 0.0,
    ),
    Category(
        "Steering Active",
        "Steering angle reaches at least 15 degrees.",
        lambda a: (a.steering_max_abs_deg or 0) >= 15,
        lambda a: a.steering_max_abs_deg or 0.0,
    ),
    Category(
        "Curves",
        "Steering angle standard deviation at or above 5 degrees.",
        lambda a: (a.steering_stdev_deg or 0) >= 5,
        lambda a: a.steering_stdev_deg or 0.0,
    ),
    Category(
        "Slow Driving",
        "Average wheel speed at or below 35 km/h.",
        lambda a: a.average_speed_kmh is not None and a.average_speed_kmh <= 35,
        lambda a: -(a.average_speed_kmh or 0.0),
    ),
    Category(
        "High CAN Rate",
        "More frames per second than most segments in this chunk.",
        lambda a: a.frames_per_second >= 2000,
        lambda a: a.frames_per_second,
    ),
    Category(
        "Mixed",
        "Both a wide speed span and noticeable steering.",
        lambda a: (a.speed_range_kmh or 0) >= 15 and (a.steering_stdev_deg or 0) >= 2,
        lambda a: (a.speed_range_kmh or 0) * (a.steering_stdev_deg or 0),
    ),
]


@dataclass(slots=True)
class Recommendation:
    scenario_rank: int
    segment_path: str
    route: str
    segment_index: int
    category: str
    reason: str
    average_speed_kmh: float | None
    speed_range_kmh: float | None
    steering_stdev_deg: float | None
    frames_per_second: float
    duration_sec: float

    def as_dict(self) -> dict:
        return asdict(self)


def _feature_vector(a: SegmentAnalysis) -> tuple[float, float, float]:
    """Normalised (speed, speed span, steering activity) for diversity."""
    return (
        (a.average_speed_kmh or 0.0) / 120.0,
        (a.speed_range_kmh or 0.0) / 60.0,
        (a.steering_stdev_deg or 0.0) / 20.0,
    )


def _distance(a: SegmentAnalysis, b: SegmentAnalysis) -> float:
    return math.dist(_feature_vector(a), _feature_vector(b))


def recommend(analyses: Sequence[SegmentAnalysis], *, count: int = 20,
              minimum_separation: float = 0.05) -> list[Recommendation]:
    """Pick a varied shortlist.

    Two rules, applied together:

    * **Round robin over categories**, so the list is not twenty near-identical
      motorway minutes just because those score highest overall.
    * **A minimum separation** in the (speed, speed span, steering) feature
      space, so two segments from the same stretch of road do not both get in.
      Relaxed automatically if that would leave the list short -- a shortlist
      that is too small is worse than one that is slightly repetitive.
    """
    usable = [a for a in analyses if a.is_usable]
    if not usable:
        return []

    buckets: dict[str, list[SegmentAnalysis]] = {}
    for category in CATEGORIES:
        matched = [a for a in usable if category.matches(a)]
        matched.sort(key=category.score, reverse=True)
        if matched:
            buckets[category.name] = matched

    chosen: list[tuple[str, SegmentAnalysis]] = []
    chosen_paths: set[str] = set()

    def try_take(name: str, candidates: list[SegmentAnalysis], separation: float) -> bool:
        for candidate in candidates:
            if candidate.segment_path in chosen_paths:
                continue
            if any(_distance(candidate, other) < separation for _, other in chosen):
                continue
            chosen.append((name, candidate))
            chosen_paths.add(candidate.segment_path)
            return True
        return False

    separation = minimum_separation
    while len(chosen) < count:
        progressed = False
        for name in list(buckets):
            if len(chosen) >= count:
                break
            if try_take(name, buckets[name], separation):
                progressed = True

        if not progressed:
            if separation <= 0:
                break
            # Nothing new is far enough away; loosen the requirement and retry.
            separation = 0.0 if separation < 0.01 else separation / 2
            continue

    # Fall back to plain frame count if the categories could not fill the list.
    if len(chosen) < count:
        for candidate in sorted(usable, key=lambda a: a.total_frame_count, reverse=True):
            if len(chosen) >= count:
                break
            if candidate.segment_path not in chosen_paths:
                chosen.append(("Mixed", candidate))
                chosen_paths.add(candidate.segment_path)

    recommendations: list[Recommendation] = []
    for rank, (name, analysis) in enumerate(chosen[:count], start=1):
        recommendations.append(Recommendation(
            scenario_rank=rank,
            segment_path=analysis.segment_path,
            route=analysis.route,
            segment_index=analysis.segment_index,
            category=name,
            reason=next(c.description for c in CATEGORIES if c.name == name),
            average_speed_kmh=analysis.average_speed_kmh,
            speed_range_kmh=analysis.speed_range_kmh,
            steering_stdev_deg=analysis.steering_stdev_deg,
            frames_per_second=analysis.frames_per_second,
            duration_sec=analysis.duration_sec,
        ))
    return recommendations


def build_document(analyses: Sequence[SegmentAnalysis],
                   recommendations: Sequence[Recommendation],
                   *, source: str, dbc_profile: str) -> dict:
    usable = [a for a in analyses if a.is_usable]
    speeds = [a.average_speed_kmh for a in usable if a.average_speed_kmh is not None]

    return {
        "format_version": ANALYSIS_FORMAT_VERSION,
        "source": source,
        "dbc_profile": dbc_profile,
        "segment_count": len(analyses),
        "usable_segment_count": len(usable),
        "failed_segment_count": len(analyses) - len(usable),
        "summary": {
            "total_duration_sec": round(sum(a.duration_sec for a in usable), 1),
            "average_speed_kmh": round(statistics.fmean(speeds), 2) if speeds else None,
            "median_frames_per_second": round(
                statistics.median([a.frames_per_second for a in usable]), 1)
            if usable else None,
        },
        "categories": [
            {"name": c.name, "description": c.description,
             "matching_segments": sum(1 for a in usable if c.matches(a))}
            for c in CATEGORIES
        ],
        "recommendations": [r.as_dict() for r in recommendations],
        "segments": [a.as_dict() for a in analyses],
    }


def write(path: Path | str, document: dict) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n",
                    encoding="utf-8")


def read(path: Path | str) -> dict:
    document = json.loads(Path(path).read_text(encoding="utf-8"))
    version = document.get("format_version")
    if version != ANALYSIS_FORMAT_VERSION:
        raise ValueError(
            f"analysis format_version {version} is not supported by this build "
            f"(which reads version {ANALYSIS_FORMAT_VERSION})")
    return document


def selected_segments(document: dict, count: int = 0) -> list[Path]:
    """The recommended segment directories, in rank order."""
    recommendations = document.get("recommendations", [])
    if count:
        recommendations = recommendations[:count]
    return [Path(r["segment_path"]) for r in recommendations]
