"""Command line entry point for the Scenario Builder.

    python -m scenario_builder build --input <chunk dir> --output ./Scenarios
    python -m scenario_builder inspect --input <segment dir>
    python -m scenario_builder dbc-rank --input <segment dir>
"""

from __future__ import annotations

import argparse
from dataclasses import replace
import json
import sys
import traceback
from pathlib import Path

from . import analyze_cli, builder, canbin, comma2k19, playlists, stress, telemetry, validation
from .dbc import parse as parse_dbc
from .progress import report as report_progress

# opendbc's own platform table maps TOYOTA_RAV4 (RAV4 2016-2018, Toyota Safety
# Sense P) to these two files: opendbc/car/toyota/values.py,
#   TOYOTA_RAV4 = PlatformConfig(..., dbc_dict('toyota_new_mc_pt_generated',
#                                              'toyota_adas'))
# The choice is re-checked against processed_log on every build; see
# validation.json in each package.
DEFAULT_RAV4_DBC = ["toyota_new_mc_pt_generated.dbc", "toyota_adas.dbc"]
DEFAULT_RAV4_PROFILE = "toyota_rav4_2017"
DEFAULT_CIVIC_DBC = ["honda_civic_touring_2016_can_generated.dbc", "acura_ilx_2016_nidec.dbc"]
DEFAULT_CIVIC_PROFILE = "honda_civic_2016"


def config_for_vehicle(config, segment, dbc_dir, use_default):
    if use_default and segment.vehicle() == ("Honda", "Civic"):
        return replace(config, dbc_paths=_resolve_dbc(dbc_dir, DEFAULT_CIVIC_DBC),
                       dbc_profile=DEFAULT_CIVIC_PROFILE, candidate_dbc_paths={},
                       scenario_prefix="civic" if config.scenario_prefix == "rav4" else config.scenario_prefix)
    return config


def reusable_package(path, segment, profile):
    try:
        document = json.loads((path / "scenario.json").read_text(encoding="utf-8"))
        return (document["route"] == segment.route and
                document["segment"] == segment.segment_index and
                document["dbc_profile"] == profile and bool(document["video"]) and
                (path / document["video"]).is_file() and
                (path / "dbc/signals.json").is_file() and
                all((path / "can" / name).is_file() for name in document["can"].values()))
    except (OSError, ValueError, KeyError):
        return False

# Alternates scored alongside the selection so the manifest can show it won on
# evidence rather than on assertion.
CANDIDATE_DBCS = {
    "toyota_nodsu": ["toyota_nodsu_pt_generated.dbc", "toyota_adas.dbc"],
    "toyota_tnga_k": ["toyota_tnga_k_pt_generated.dbc", "toyota_adas.dbc"],
}


def _resolve_dbc(dbc_dir: Path, names: list[str]) -> list[Path]:
    resolved = []
    for name in names:
        path = dbc_dir / name
        if not path.is_file():
            raise SystemExit(
                f"DBC not found: {path}\n"
                f"Point --dbc-dir at an opendbc checkout's opendbc/dbc directory. "
                f"The *_generated.dbc files are produced by running\n"
                f"  python opendbc/dbc/generator/generator.py\n"
                f"inside that checkout.")
        resolved.append(path)
    return resolved


def _add_common(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--input", required=True, type=Path,
                        help="comma2k19 chunk directory, route directory or segment directory")
    parser.add_argument("--dbc-dir", type=Path, default=None,
                        help="opendbc/dbc directory holding the .dbc files")
    parser.add_argument("--dbc", nargs="+", default=DEFAULT_RAV4_DBC,
                        help="DBC file names to merge (first one is primary)")
    parser.add_argument("--dbc-profile", default=DEFAULT_RAV4_PROFILE)


def cmd_build(args: argparse.Namespace) -> int:
    segments = comma2k19.find_segments(args.input)
    segments = [s for s in segments if s.is_complete()]
    if args.limit:
        segments = segments[:args.limit]
    if not segments:
        print(f"No complete comma2k19 segments found under {args.input}", file=sys.stderr)
        return 2

    dbc_dir = args.dbc_dir or (args.input / "dbc")
    config = builder.BuilderConfig(
        output_root=args.output,
        dbc_paths=_resolve_dbc(dbc_dir, args.dbc),
        dbc_profile=args.dbc_profile,
        candidate_dbc_paths={name: _resolve_dbc(dbc_dir, files)
                             for name, files in CANDIDATE_DBCS.items()
                             if not args.no_candidates},
        crf=args.crf, preset=args.preset, max_width=args.max_width,
        include_tx_echo=not args.exclude_tx_echo,
        write_jsonl=args.jsonl, skip_video=args.skip_video,
        playback_bitrate=args.playback_bitrate,
        scenario_prefix=args.prefix, overwrite=args.overwrite,
    )
    config.output_root.mkdir(parents=True, exist_ok=True)

    print(f"Found {len(segments)} segment(s) under {args.input}")
    report_progress("build", 0, len(segments), "Starting conversion")
    built: list[dict] = []
    failures: list[tuple[str, str]] = []
    existing_sources = {}
    if args.skip_existing:
        for manifest_path in config.output_root.glob("*/scenario.json"):
            try:
                saved = json.loads(manifest_path.read_text(encoding="utf-8"))
                existing_sources[(saved["route"], saved["segment"], saved["dbc_profile"])] = manifest_path.parent
            except (OSError, ValueError, KeyError):
                continue
    for index, segment in enumerate(segments, start=args.start_index):
        selected_config = config_for_vehicle(config, segment, dbc_dir,
            args.dbc == DEFAULT_RAV4_DBC and args.dbc_profile == DEFAULT_RAV4_PROFILE)
        target = config.output_root / f"{selected_config.scenario_prefix}_{index:03d}"
        previous = existing_sources.get((segment.route, segment.segment_index, selected_config.dbc_profile), target)
        if args.skip_existing and reusable_package(previous, segment, selected_config.dbc_profile):
            # Reuse the package, but top up the GNSS/IMU sidecar if it predates
            # the telemetry feature -- cheap (no video re-encode) and idempotent.
            if telemetry.ensure_for_package(previous, segment):
                print(f"  ++ Added telemetry to {previous.name}", flush=True)
            print(f"  == Already converted: {previous.name}", flush=True)
            built.append(json.loads((previous / "scenario.json").read_text(encoding="utf-8")))
            report_progress("build", index - args.start_index + 1, len(segments), f"Reused {previous.name}")
            continue
        try:
            result = builder.build_segment(segment, selected_config, scenario_index=index,
                                           progress=lambda m: print("  " + m))
        except Exception as error:                      # keep going, report at the end
            failures.append((str(segment.path), f"{type(error).__name__}: {error}"))
            print(f"  !! {segment.path}: {error}", file=sys.stderr)
            if args.traceback:
                traceback.print_exc()
            report_progress("build", index - args.start_index + 1, len(segments), f"Failed: {segment.path.name}")
            continue
        built.append(json.loads((result.path / "scenario.json").read_text(encoding="utf-8")))
        playlists.rebuild(config.output_root)
        buses = ", ".join(f"bus{b}={result.frame_counts[b]}" for b in result.buses)
        print(f"  == {result.scenario_id}: {result.duration_sec:.1f}s  {buses}  "
              f"default=bus{result.default_bus}  score={result.validation_score}  "
              f"tags={','.join(result.tags) or '-'}")
        for warning in result.warnings:
            print(f"     warning: {warning}")
        report_progress("build", index - args.start_index + 1, len(segments), f"Converted {result.scenario_id}")

    if built:
        playlists.rebuild(config.output_root)
        print(f"\nWrote {len(built)} scenario(s) and playlist.json to {config.output_root}")
    if failures:
        print(f"\n{len(failures)} segment(s) failed:", file=sys.stderr)
        for path, message in failures:
            print(f"  {path}: {message}", file=sys.stderr)
    return 0 if built and not failures else 1


def cmd_inspect(args: argparse.Namespace) -> int:
    segments = [s for s in comma2k19.find_segments(args.input) if s.is_complete()]
    if not segments:
        print(f"No complete segments under {args.input}", file=sys.stderr)
        return 2
    for segment in segments[:args.limit or len(segments)]:
        make, model = segment.vehicle()
        print(f"\n{segment.path}")
        print(f"  route={segment.route} segment={segment.segment_index} "
              f"vehicle={make} {model}")
        raw = comma2k19.read_raw_log_can(segment.raw_log)
        for bus in sorted(raw.buses):
            frames = raw.buses[bus]
            ids = {f.can_id for f in frames}
            echo = sum(1 for f in frames if f.tx_echo)
            print(f"  bus {bus}: {len(frames):7d} frames  {len(ids):3d} ids  "
                  f"{echo:6d} tx-echo  {frames[-1].timestamp_us / 1e6:.3f}s")
        processed = comma2k19.read_processed_can(segment)
        print(f"  processed_log/CAN: {', '.join(sorted(processed)) or '(none)'}")
    return 0


def cmd_dbc_rank(args: argparse.Namespace) -> int:
    """Score candidate DBCs against processed_log without building a package."""
    segments = [s for s in comma2k19.find_segments(args.input) if s.is_complete()]
    if not segments:
        print(f"No complete segments under {args.input}", file=sys.stderr)
        return 2
    segment = segments[0]
    dbc_dir = args.dbc_dir or (args.input / "dbc")

    raw = comma2k19.read_raw_log_can(segment.raw_log)
    processed = comma2k19.read_processed_can(segment)
    databases = {args.dbc_profile: builder.load_databases(_resolve_dbc(dbc_dir, args.dbc))}
    for name, files in CANDIDATE_DBCS.items():
        databases[name] = builder.load_databases(_resolve_dbc(dbc_dir, files))

    bus, reason = builder._choose_default_bus(raw.buses, databases[args.dbc_profile])
    print(f"{segment.path}\n  default bus: {reason}\n")
    for report in validation.rank_databases(raw.buses[bus], databases, processed,
                                            raw.first_frame_mono_ns / 1e9):
        print(f"{report.dbc_profile:24s} score={report.score():9.5f} "
              f"decodable_ids={len(report.decodable_message_ids):3d}")
        for c in report.comparisons:
            print(f"    {c.reference:16s} <- {c.source:48s} "
                  f"n={c.samples:5d} RMSE={c.rmse:10.5f} {c.unit_reference:4s} "
                  f"r={c.correlation}")
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    """Re-read a built scenario package and check it is internally consistent."""
    root = args.input
    manifests = sorted(root.glob("*/scenario.json")) if root.is_dir() else []
    if not manifests:
        print(f"No scenario.json found under {root}", file=sys.stderr)
        return 2
    problems = 0
    report_progress("verify", 0, len(manifests), "Checking scenario packages")
    for verified, path in enumerate(manifests, start=1):
        document = json.loads(path.read_text(encoding="utf-8"))
        base = path.parent
        for bus, name in document["can"].items():
            file = base / "can" / name
            if not file.is_file():
                print(f"  MISSING {file}", file=sys.stderr)
                problems += 1
                continue
            header, frames = canbin.read(file)
            if header.frame_count != len(frames):
                print(f"  BAD COUNT {file}", file=sys.stderr)
                problems += 1
            bad = [f for f in frames if f.dlc > 8 or len(f.data) != f.dlc]
            if bad:
                print(f"  BAD FRAMES {file}: {len(bad)}", file=sys.stderr)
                problems += 1
        if document["video"] and not (base / document["video"]).is_file():
            print(f"  MISSING VIDEO {base / document['video']}", file=sys.stderr)
            problems += 1
        print(f"  ok {document['scenario_id']}: buses={document['available_buses']} "
              f"default={document['default_bus']} {document['duration_sec']}s")
        report_progress("verify", verified, len(manifests), document["scenario_id"])
    print(f"\n{len(manifests)} scenario(s), {problems} problem(s)")
    return 1 if problems else 0


def cmd_stress(args: argparse.Namespace) -> int:
    path = stress.build_package(
        args.source, args.output, scenario_id=args.scenario_id,
        duration_sec=args.duration, target_wire_bps=args.target_load,
        playback_bitrate=args.playback_bitrate, bus=args.bus)
    document = json.loads((path / "scenario.json").read_text(encoding="utf-8"))
    details = document["stress_test"]
    print(f"Wrote {path}")
    print(f"  frames={document['bus_statistics'][0]['frame_count']} "
          f"wire_load={details['calculated_wire_load_bps']:.3f} bit/s "
          f"duration={document['duration_sec']:.3f}s")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="scenario_builder",
        description="Build CAN Replay Scenario Packages from comma2k19 segments.")
    sub = parser.add_subparsers(dest="command", required=True)

    build_parser = sub.add_parser("build", help="build scenario packages")
    _add_common(build_parser)
    build_parser.add_argument("--output", required=True, type=Path)
    build_parser.add_argument("--limit", type=int, default=0,
                              help="build at most N segments (0 = all)")
    build_parser.add_argument("--start-index", type=int, default=1)
    build_parser.add_argument("--prefix", default="rav4")
    build_parser.add_argument("--max-width", type=int, default=0, help="maximum video width; 0 keeps source resolution")
    build_parser.add_argument("--crf", type=int, default=20)
    build_parser.add_argument("--preset", default="medium")
    build_parser.add_argument("--jsonl", action="store_true",
                              help="also write debug/bus_N.jsonl")
    build_parser.add_argument("--skip-video", action="store_true")
    build_parser.add_argument("--exclude-tx-echo", action="store_true",
                              help="drop frames the panda transmitted (src & 0x80)")
    build_parser.add_argument("--no-candidates", action="store_true",
                              help="skip scoring alternate DBCs")
    build_parser.add_argument(
        "--playback-bitrate", type=int, default=500_000,
        help="bench bit rate this package is meant to be replayed at "
             "(default 500000). The vehicle's own bus rate is not documented by "
             "comma2k19 and is always recorded as null.")
    build_parser.add_argument("--overwrite", action="store_true", default=True)
    build_parser.add_argument("--traceback", action="store_true")
    build_parser.add_argument("--skip-existing", action="store_true", help="reuse complete packages from the same source segment")
    build_parser.set_defaults(func=cmd_build)

    inspect_parser = sub.add_parser("inspect", help="summarise segments without building")
    inspect_parser.add_argument("--input", required=True, type=Path)
    inspect_parser.add_argument("--limit", type=int, default=0)
    inspect_parser.set_defaults(func=cmd_inspect)

    rank_parser = sub.add_parser("dbc-rank", help="score candidate DBCs against processed_log")
    _add_common(rank_parser)
    rank_parser.set_defaults(func=cmd_dbc_rank)

    analyze_cli.add_arguments(
        sub,
        add_common=_add_common,
        resolve_dbc=_resolve_dbc,
        default_dbc=DEFAULT_RAV4_DBC,
        default_profile=DEFAULT_RAV4_PROFILE,
        candidate_dbcs=CANDIDATE_DBCS,
    )

    verify_parser = sub.add_parser("verify", help="check a built Scenarios directory")
    verify_parser.add_argument("--input", required=True, type=Path)
    verify_parser.set_defaults(func=cmd_verify)

    stress_parser = sub.add_parser("stress", help="build a high-load scenario package")
    stress_parser.add_argument("--source", required=True, type=Path,
                               help="existing RAV4 scenario package")
    stress_parser.add_argument("--output", required=True, type=Path,
                               help="Scenarios directory")
    stress_parser.add_argument("--scenario-id", default="rav4_pcan_stress_450k_5min")
    stress_parser.add_argument("--duration", type=float, default=300.0)
    stress_parser.add_argument("--target-load", type=int, default=450_000)
    stress_parser.add_argument("--playback-bitrate", type=int, default=500_000)
    stress_parser.add_argument("--bus", type=int, default=0)
    stress_parser.set_defaults(func=cmd_stress)

    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
