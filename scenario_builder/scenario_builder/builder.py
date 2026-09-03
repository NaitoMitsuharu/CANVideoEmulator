"""Turn comma2k19 segments into Scenario Packages.

One segment (~1 minute) becomes one scenario (requirement 4).  Layout produced
(requirement 31)::

    <scenario_id>/
      scenario.json
      video.mp4
      thumbnail.jpg
      can/bus_0.canbin, bus_1.canbin, ...
      dbc/original.dbc
      dbc/signals.json
      validation.json
      conversion.json
      debug/bus_0.jsonl          (only with --jsonl)
"""

from __future__ import annotations

import json
import shutil
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Sequence

import numpy as np

from . import (canbin, comma2k19, manifest, signals as signals_mod, telemetry,
               validation, video)
from .dbc import Database, parse as parse_dbc

DATASET_NAME = "comma2k19"


@dataclass(slots=True)
class BuilderConfig:
    """Everything the builder needs that is not derived from the segment."""

    output_root: Path
    dbc_paths: list[Path]
    dbc_profile: str
    candidate_dbc_paths: dict[str, list[Path]] = field(default_factory=dict)
    video_fps: float = video.DEFAULT_FPS
    max_width: int = 0
    crf: int = 20
    preset: str = "medium"
    include_tx_echo: bool = True
    write_jsonl: bool = False
    skip_video: bool = False
    # The vehicle's own bus rate: unknown for comma2k19 and never guessed.
    original_bitrate: int | None = manifest.UNKNOWN_ORIGINAL_BITRATE
    # The rate this package is meant to be replayed at on the bench.
    playback_bitrate: int | None = manifest.DEFAULT_PLAYBACK_BITRATE
    scenario_prefix: str = "rav4"
    overwrite: bool = True


@dataclass(slots=True)
class BuildResult:
    scenario_id: str
    path: Path
    duration_sec: float
    buses: list[int]
    default_bus: int
    frame_counts: dict[int, int]
    validation_score: float | None
    tags: list[str]
    warnings: list[str] = field(default_factory=list)


def load_databases(paths: Sequence[Path]) -> Database:
    from .dbc import merge
    return merge(*[parse_dbc(p) for p in paths])


def _video_can_offset_ms(segment: comma2k19.Segment, first_can_mono_ns: int,
                         warnings: list[str]) -> float:
    """Milliseconds from CAN t=0 to the first camera frame.

    ``global_pose/frame_times`` holds each camera frame's boot-monotonic
    timestamp, the same clock ``Event.logMonoTime`` uses, so the two media share
    one time base exactly (requirement 12).  When the file is absent the offset
    is reported as 0 and a warning is recorded -- it is never invented.
    """
    frame_times = comma2k19.read_frame_times(segment)
    if frame_times is None or frame_times.size == 0:
        warnings.append(
            "global_pose/frame_times is missing; video_can_offset_ms is set to 0 "
            "and video/CAN alignment may be off by up to one frame period.")
        return 0.0
    return (float(frame_times[0]) - first_can_mono_ns / 1e9) * 1000.0


def _measured_fps(segment: comma2k19.Segment, fallback: float,
                  warnings: list[str]) -> tuple[float, int | None]:
    """Frame rate measured from frame_times rather than assumed."""
    frame_times = comma2k19.read_frame_times(segment)
    if frame_times is None or frame_times.size < 2:
        warnings.append(f"frame_times unusable; falling back to {fallback} fps.")
        return fallback, None
    span = float(frame_times[-1] - frame_times[0])
    if span <= 0:
        warnings.append(f"frame_times span is not positive; using {fallback} fps.")
        return fallback, int(frame_times.size)
    return round((frame_times.size - 1) / span, 6), int(frame_times.size)


def _choose_default_bus(buses: dict[int, list[canbin.CanFrame]],
                        database: Database) -> tuple[int, str]:
    """Pick the bus to preselect, on a measurable criterion (requirement 27).

    The chosen rule is "most CAN IDs this scenario's DBC can actually decode",
    with total frame count as the tie-break.  That is a property of this
    recording plus this DBC, so it can be stated in scenario.json and checked.
    Buses are not labelled Powertrain/ADAS/etc. anywhere -- requirement 28 --
    because nothing in the dataset documents what each captured bus was wired to.
    """
    known = set(database.by_frame_id())
    best_bus, best_key, best_hits = None, (-1, -1), 0
    for bus in sorted(buses):
        frames = buses[bus]
        hits = len({f.can_id for f in frames} & known)
        key = (hits, len(frames))
        if key > best_key:
            best_bus, best_key, best_hits = bus, key, hits
    if best_bus is None:
        raise ValueError("segment contains no CAN frames on any bus")
    return best_bus, (
        f"bus {best_bus} carries the most CAN IDs decodable by "
        f"{'/'.join(database.source_files)} ({best_hits} of "
        f"{len({f.can_id for f in buses[best_bus]})} observed IDs)")


def build_segment(segment: comma2k19.Segment, config: BuilderConfig, *,
                  scenario_index: int,
                  progress: Callable[[str], None] = lambda _: None) -> BuildResult:
    warnings: list[str] = []
    make, model = segment.vehicle()
    scenario_id = f"{config.scenario_prefix}_{scenario_index:03d}"
    out_dir = config.output_root / scenario_id

    if out_dir.exists():
        if not config.overwrite:
            raise FileExistsError(f"{out_dir} already exists (use --overwrite)")
        shutil.rmtree(out_dir)
    (out_dir / "can").mkdir(parents=True, exist_ok=True)
    (out_dir / "dbc").mkdir(parents=True, exist_ok=True)

    progress(f"[{scenario_id}] reading raw_log.bz2")
    raw = comma2k19.read_raw_log_can(segment.raw_log,
                                     include_tx_echo=config.include_tx_echo)
    if not raw.buses:
        raise ValueError(f"{segment.path} contains no CAN frames")

    database = load_databases(config.dbc_paths)
    # Select the vehicle bus with the primary powertrain DBC. Radar can expose
    # more message IDs on a separate bus (notably Civic), so merging radar first
    # would otherwise make the radar-only bus the playback default.
    default_bus, default_bus_reason = _choose_default_bus(raw.buses, load_databases(config.dbc_paths[:1]))

    progress(f"[{scenario_id}] writing CAN timelines")
    can_files: dict[int, str] = {}
    frame_counts: dict[int, int] = {}
    stats: list[manifest.BusStatistics] = []
    for bus in sorted(raw.buses):
        frames = raw.buses[bus]
        name = f"bus_{bus}.canbin"
        canbin.write(out_dir / "can" / name, bus, frames)
        can_files[bus] = name
        frame_counts[bus] = len(frames)
        stats.append(manifest.bus_statistics(bus, frames))
        if config.write_jsonl:
            debug = out_dir / "debug" / f"bus_{bus}.jsonl"
            debug.parent.mkdir(parents=True, exist_ok=True)
            with debug.open("w", encoding="utf-8") as handle:
                for line in canbin.to_jsonl(frames, bus):
                    handle.write(line + "\n")

    fps, frame_count = _measured_fps(segment, config.video_fps, warnings)
    duration_sec = max(raw.duration_us / 1e6,
                       (frame_count / fps) if frame_count and fps else 0.0)

    conversion: dict = {}
    video_name = "video.mp4"
    thumbnail_name = "thumbnail.jpg"
    if config.skip_video:
        warnings.append("video conversion skipped (--skip-video); "
                        "the package has no playable video.")
        video_name = ""
        thumbnail_name = ""
    else:
        progress(f"[{scenario_id}] converting video ({fps:.3f} fps)")
        result = video.convert(segment.video_hevc, out_dir / video_name,
                               fps=fps, crf=config.crf, preset=config.preset, max_width=config.max_width)
        video.make_thumbnail(segment.preview, out_dir / video_name,
                             out_dir / thumbnail_name)
        result.thumbnail = thumbnail_name
        conversion = result.as_dict()
        if result.duration_sec:
            duration_sec = max(duration_sec, result.duration_sec)

    progress(f"[{scenario_id}] validating against processed_log")
    processed = comma2k19.read_processed_can(segment)
    t_offset = raw.first_frame_mono_ns / 1e9
    default_frames = raw.buses[default_bus]

    candidates: dict[str, Database] = {config.dbc_profile: database}
    for name, paths in config.candidate_dbc_paths.items():
        if name != config.dbc_profile:
            candidates[name] = load_databases(paths)
    reports = validation.rank_databases(default_frames, candidates, processed, t_offset)
    chosen = next(r for r in reports if r.dbc_profile == config.dbc_profile)
    if reports[0].dbc_profile != config.dbc_profile:
        warnings.append(
            f"candidate DBC '{reports[0].dbc_profile}' scored better "
            f"({reports[0].score():.5f}) than the selected "
            f"'{config.dbc_profile}' ({chosen.score():.5f}); review dbc_profile.")
    if not processed:
        warnings.append("processed_log/CAN is absent; no decode validation was possible.")

    validation_document = {
        "format_version": 1,
        "scenario_id": scenario_id,
        "validated_bus": default_bus,
        "reference": "comma2k19 processed_log/CAN",
        "selected": chosen.as_dict(),
        "candidates": [r.as_dict() for r in reports],
    }
    (out_dir / "validation.json").write_text(
        json.dumps(validation_document, indent=2) + "\n", encoding="utf-8")

    progress(f"[{scenario_id}] generating signals.json")
    for path in config.dbc_paths:
        shutil.copyfile(path, out_dir / "dbc" / path.name)
    primary_dbc = config.dbc_paths[0].name
    signals_mod.write(out_dir / "dbc" / "signals.json",
                      signals_mod.build(database, default_frames,
                                        profile=config.dbc_profile,
                                        vehicle=f"{make} {model}",
                                        bus_index=default_bus))

    tags, evidence = manifest.auto_tags(default_frames, database)
    offset_ms = _video_can_offset_ms(segment, raw.first_frame_mono_ns, warnings)

    progress(f"[{scenario_id}] extracting GNSS/IMU telemetry")
    telemetry_name: str | None = None
    telemetry_document = telemetry.build_telemetry(
        segment, raw.first_frame_mono_ns, warnings=warnings)
    if telemetry_document is not None:
        telemetry_name = "telemetry.json"
        (out_dir / telemetry_name).write_text(
            json.dumps(telemetry_document, ensure_ascii=False) + "\n",
            encoding="utf-8")

    document = manifest.build_scenario_json(
        scenario_id=scenario_id,
        title=f"{model} #{scenario_index:03d}",
        vehicle_make=make, vehicle_model=model, vehicle_year=None,
        dataset=DATASET_NAME, route=segment.route, segment=segment.segment_index,
        source_dongle_id=segment.dongle_id,
        duration_sec=duration_sec,
        video=video_name, thumbnail=thumbnail_name,
        available_buses=sorted(raw.buses), default_bus=default_bus,
        default_bus_reason=default_bus_reason,
        original_bitrate=config.original_bitrate,
        playback_bitrate=config.playback_bitrate,
        video_can_offset_ms=offset_ms,
        dbc_profile=config.dbc_profile,
        tags=tags, tag_evidence=evidence,
        description=(f"comma2k19 {segment.route} segment {segment.segment_index}, "
                     f"{duration_sec:.1f} s of recorded {make} {model} driving."),
        can_files=can_files, bus_stats=stats,
        video_fps=fps, video_frame_count=frame_count,
        telemetry=telemetry_name,
    )
    document["dbc_primary_file"] = primary_dbc
    document["build_warnings"] = warnings
    (out_dir / "scenario.json").write_text(
        json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    if conversion:
        conversion["scenario_id"] = scenario_id
        conversion["video_can_offset_ms"] = round(offset_ms, 3)
        conversion["measured_fps"] = fps
        (out_dir / "conversion.json").write_text(
            json.dumps(conversion, indent=2) + "\n", encoding="utf-8")

    return BuildResult(
        scenario_id=scenario_id, path=out_dir, duration_sec=duration_sec,
        buses=sorted(raw.buses), default_bus=default_bus, frame_counts=frame_counts,
        validation_score=None if chosen.score() == float("inf") else round(chosen.score(), 6),
        tags=tags, warnings=warnings,
    )
