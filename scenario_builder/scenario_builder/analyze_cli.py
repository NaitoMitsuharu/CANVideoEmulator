"""The ``analyze`` and ``build-selected`` commands.

Kept out of ``cli.py`` because the two-stage workflow they implement -- survey a
whole chunk cheaply, then transcode only what a human approved -- is a distinct
concern from the single-pass ``build``.
"""

from __future__ import annotations

import argparse
import json
import sys
import traceback
from pathlib import Path

from . import analyze as analyze_mod
from . import builder, comma2k19, playlists


def add_arguments(sub, *, add_common, resolve_dbc, default_dbc, default_profile,
                  candidate_dbcs) -> None:
    """Register both subcommands on an argparse subparser action."""
    analyze_parser = sub.add_parser(
        "analyze",
        help="survey a chunk and recommend segments (reads CAN only, no video)")
    add_common(analyze_parser)
    analyze_parser.add_argument("--output", type=Path,
                                default=Path("scenario_analysis.json"))
    analyze_parser.add_argument("--limit", type=int, default=0,
                                help="analyse at most N segments (0 = all)")
    analyze_parser.add_argument("--count", type=int, default=20,
                                help="how many segments to recommend")
    analyze_parser.set_defaults(
        func=lambda args: cmd_analyze(args, resolve_dbc=resolve_dbc))

    selected_parser = sub.add_parser(
        "build-selected",
        help="build only the segments an analyze run recommended")
    selected_parser.add_argument("--analysis", required=True, type=Path)
    selected_parser.add_argument("--output", required=True, type=Path)
    selected_parser.add_argument("--dbc-dir", type=Path, default=None)
    selected_parser.add_argument("--dbc", nargs="+", default=default_dbc)
    selected_parser.add_argument("--dbc-profile", default=default_profile)
    selected_parser.add_argument("--count", type=int, default=0,
                                 help="build only the top N recommendations (0 = all)")
    selected_parser.add_argument("--start-index", type=int, default=1)
    selected_parser.add_argument("--prefix", default="rav4")
    selected_parser.add_argument("--max-width", type=int, default=0, help="maximum video width; 0 keeps source resolution")
    selected_parser.add_argument("--crf", type=int, default=20)
    selected_parser.add_argument("--preset", default="medium")
    selected_parser.add_argument("--playback-bitrate", type=int, default=500_000)
    selected_parser.add_argument("--jsonl", action="store_true")
    selected_parser.add_argument("--skip-video", action="store_true")
    selected_parser.add_argument("--no-candidates", action="store_true")
    selected_parser.add_argument("--traceback", action="store_true")
    selected_parser.set_defaults(
        func=lambda args: cmd_build_selected(
            args, resolve_dbc=resolve_dbc, candidate_dbcs=candidate_dbcs))


def cmd_analyze(args: argparse.Namespace, *, resolve_dbc) -> int:
    """Survey a chunk and recommend segments, transcoding nothing."""
    dbc_dir = args.dbc_dir or (args.input / "dbc")
    database = builder.load_databases(resolve_dbc(dbc_dir, args.dbc))

    print(f"Scanning {args.input} ...")
    analyses = analyze_mod.analyze_chunk(
        args.input, database, limit=args.limit,
        progress=lambda message: print("  " + message))

    usable = [a for a in analyses if a.is_usable]
    print()
    print(f"{len(usable)} usable of {len(analyses)} segment(s)")
    for failed in (a for a in analyses if not a.is_usable):
        print(f"  !! {failed.segment_path}: {failed.error}", file=sys.stderr)

    if not usable:
        print("Nothing to recommend.", file=sys.stderr)
        return 1

    recommendations = analyze_mod.recommend(analyses, count=args.count)
    document = analyze_mod.build_document(
        analyses, recommendations,
        source=str(args.input), dbc_profile=args.dbc_profile)
    analyze_mod.write(args.output, document)

    print()
    print(f"Recommended {len(recommendations)} segment(s):")
    for item in recommendations:
        speed = ("%5.0f" % item.average_speed_kmh) if item.average_speed_kmh is not None else "    -"
        span = ("%4.0f" % item.speed_range_kmh) if item.speed_range_kmh is not None else "   -"
        steer = ("%5.1f" % item.steering_stdev_deg) if item.steering_stdev_deg is not None else "    -"
        print(f"  {item.scenario_rank:3d}. {item.category:16s} seg {item.segment_index:3d}  "
              f"{speed} km/h  span {span}  steer sd {steer}  "
              f"{item.frames_per_second:6.0f} fps")

    print()
    print(f"Wrote {args.output}")
    print("Build them with:")
    print(f"  python -m scenario_builder build-selected --analysis {args.output} "
          f"--output ./Scenarios --dbc-dir {dbc_dir}")
    return 0


def cmd_build_selected(args: argparse.Namespace, *, resolve_dbc, candidate_dbcs) -> int:
    """Build only the segments an earlier ``analyze`` run recommended."""
    try:
        document = analyze_mod.read(args.analysis)
    except (OSError, ValueError) as error:
        print(f"{args.analysis}: {error}", file=sys.stderr)
        return 2

    paths = analyze_mod.selected_segments(document, args.count)
    if not paths:
        print(f"{args.analysis} contains no recommendations", file=sys.stderr)
        return 2

    missing = [p for p in paths if not (p / "raw_log.bz2").is_file()]
    if missing:
        print(f"{len(missing)} recommended segment(s) are no longer present:",
              file=sys.stderr)
        for path in missing:
            print(f"  {path}", file=sys.stderr)
        paths = [p for p in paths if p not in missing]
        if not paths:
            return 2

    dbc_dir = args.dbc_dir or (Path(document["source"]) / "dbc")
    config = builder.BuilderConfig(
        output_root=args.output,
        dbc_paths=resolve_dbc(dbc_dir, args.dbc),
        dbc_profile=args.dbc_profile,
        candidate_dbc_paths={name: resolve_dbc(dbc_dir, files)
                             for name, files in candidate_dbcs.items()
                             if not args.no_candidates},
        crf=args.crf, preset=args.preset, max_width=args.max_width,
        write_jsonl=args.jsonl, skip_video=args.skip_video,
        playback_bitrate=args.playback_bitrate,
        scenario_prefix=args.prefix, overwrite=True,
    )
    config.output_root.mkdir(parents=True, exist_ok=True)

    categories = {r["segment_path"]: r["category"]
                  for r in document.get("recommendations", [])}

    print(f"Building {len(paths)} recommended segment(s) into {args.output}")
    built: list[dict] = []
    failures: list[tuple[str, str]] = []

    for index, path in enumerate(paths, start=args.start_index):
        found = [s for s in comma2k19.find_segments(path) if s.is_complete()]
        if not found:
            failures.append((str(path), "not a usable segment directory"))
            print(f"  !! {path}: not a usable segment directory", file=sys.stderr)
            continue

        try:
            result = builder.build_segment(
                found[0], config, scenario_index=index,
                progress=lambda message: print("  " + message))
        except Exception as error:                     # keep going; report at the end
            failures.append((str(path), f"{type(error).__name__}: {error}"))
            print(f"  !! {path}: {error}", file=sys.stderr)
            if args.traceback:
                traceback.print_exc()
            continue

        manifest_path = result.path / "scenario.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

        # Carry the recommendation's category into the manifest so the generated
        # playlists group the way the analysis intended, and so the reason a
        # scenario was picked survives into the package.
        category = categories.get(str(path))
        if category:
            manifest["recommended_category"] = category
            if category not in manifest["tags"]:
                manifest["tags"].append(category)
            manifest_path.write_text(
                json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8")

        built.append(manifest)
        print(f"  == {result.scenario_id}: {result.duration_sec:.1f}s  "
              f"default=bus{result.default_bus}  category={category or '-'}")

    if built:
        playlists.rebuild(config.output_root)
        print()
        print(f"Wrote {len(built)} scenario(s) and playlist.json to {config.output_root}")

    for path, message in failures:
        print(f"  failed: {path}: {message}", file=sys.stderr)

    return 0 if built and not failures else 1
