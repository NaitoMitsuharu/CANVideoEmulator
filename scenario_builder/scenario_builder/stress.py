"""Generate a deterministic high-load timeline from recorded CAN frames."""

from __future__ import annotations

from collections.abc import Sequence
import json
from pathlib import Path
import shutil
import subprocess

from . import canbin, playlists
from .canbin import CanFrame
from .manifest import bus_statistics

CRC15_POLYNOMIAL = 0x4599
FIXED_TRAILER_BITS = 13  # CRC delimiter, ACK, EOF and intermission


def _append_bits(bits: list[int], value: int, width: int) -> None:
    bits.extend((value >> shift) & 1 for shift in range(width - 1, -1, -1))


def _crc15(bits: Sequence[int]) -> int:
    remainder = 0
    for bit in bits:
        feedback = ((remainder >> 14) & 1) ^ bit
        remainder = (remainder << 1) & 0x7FFF
        if feedback:
            remainder ^= CRC15_POLYNOMIAL
    return remainder


def _destuffed_frame_bits(frame: CanFrame) -> list[int]:
    bits = [0]  # SOF
    if frame.extended:
        _append_bits(bits, frame.can_id >> 18, 11)
        bits.extend((1, 1))  # SRR, IDE
        _append_bits(bits, frame.can_id & 0x3FFFF, 18)
        bits.extend((1 if frame.rtr else 0, 0, 0))  # RTR, r1, r0
    else:
        _append_bits(bits, frame.can_id, 11)
        bits.extend((1 if frame.rtr else 0, 0, 0))  # RTR, IDE, r0
    _append_bits(bits, frame.dlc, 4)
    if not frame.rtr:
        for byte in frame.data:
            _append_bits(bits, byte, 8)
    _append_bits(bits, _crc15(bits), 15)
    return bits


def wire_bit_count(frame: CanFrame) -> int:
    """Return classical-CAN bits occupied by one frame, including stuffing."""
    bits = _destuffed_frame_bits(frame)
    stuffed = 0
    previous = bits[0]
    run_length = 1
    for bit in bits[1:]:
        if bit == previous:
            run_length += 1
            if run_length == 5:
                stuffed += 1
                previous = 1 - bit
                run_length = 1
                continue
        else:
            run_length = 1
        previous = bit
    return len(bits) + stuffed + FIXED_TRAILER_BITS


def generate_timeline(source: Sequence[CanFrame], *, duration_sec: float,
                      target_wire_bps: int) -> list[CanFrame]:
    """Cycle ``source`` at a wire-rate target for the requested duration."""
    if not source:
        raise ValueError("source timeline is empty")
    if duration_sec <= 0:
        raise ValueError("duration_sec must be positive")
    if target_wire_bps <= 0:
        raise ValueError("target_wire_bps must be positive")

    duration_us = round(duration_sec * 1_000_000)
    frames: list[CanFrame] = []
    elapsed_bits = 0
    source_index = 0
    while True:
        timestamp_us = round(elapsed_bits * 1_000_000 / target_wire_bps)
        if timestamp_us >= duration_us:
            break
        original = source[source_index]
        frame = CanFrame(
            timestamp_us=timestamp_us,
            can_id=original.can_id,
            dlc=original.dlc,
            data=original.data,
            extended=original.extended,
            rtr=original.rtr,
        )
        frames.append(frame)
        elapsed_bits += wire_bit_count(frame)
        source_index = (source_index + 1) % len(source)
    return frames


def measured_wire_bps(frames: Sequence[CanFrame]) -> float:
    """Measure the generated timeline using the same on-wire bit model."""
    if len(frames) < 2 or frames[-1].timestamp_us == 0:
        return 0.0
    bits_before_last = sum(wire_bit_count(frame) for frame in frames[:-1])
    return bits_before_last * 1_000_000 / frames[-1].timestamp_us


def _write_video(path: Path, *, duration_sec: float) -> None:
    timestamp = (
        "drawtext=fontfile='C\\:/Windows/Fonts/consola.ttf':"
        "text='%{pts\\:hms}':fontcolor=white:fontsize=64:"
        "box=1:boxcolor=black@0.75:x=(w-text_w)/2:y=h-120"
    )
    command = [
        "ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", "smptebars=size=1280x720:rate=30",
        "-vf", timestamp, "-t", str(duration_sec), "-an",
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "28",
        "-g", "30", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
        str(path),
    ]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"ffmpeg failed creating stress video: {result.stderr.strip()}")


def _write_thumbnail(video: Path, thumbnail: Path) -> None:
    command = [
        "ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
        "-ss", "1", "-i", str(video), "-frames:v", "1", "-q:v", "2",
        str(thumbnail),
    ]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"ffmpeg failed creating stress thumbnail: {result.stderr.strip()}")


def build_package(source_package: Path, output_root: Path, *, scenario_id: str,
                  duration_sec: float, target_wire_bps: int,
                  playback_bitrate: int = 500_000, bus: int | None = None) -> Path:
    """Build a complete high-load scenario from an existing package."""
    source_package = Path(source_package)
    output_root = Path(output_root)
    source_manifest = json.loads(
        (source_package / "scenario.json").read_text(encoding="utf-8"))
    selected_bus = source_manifest["default_bus"] if bus is None else bus
    can_name = source_manifest["can"].get(str(selected_bus))
    if can_name is None:
        raise ValueError(f"source package has no bus {selected_bus}")
    if target_wire_bps > playback_bitrate:
        raise ValueError("target wire load must not exceed the playback bitrate")

    _, source_frames = canbin.read(source_package / "can" / can_name)
    frames = generate_timeline(source_frames, duration_sec=duration_sec,
                               target_wire_bps=target_wire_bps)
    actual_wire_bps = measured_wire_bps(frames)

    destination = output_root / scenario_id
    if (destination / "scenario.json").exists():
        raise FileExistsError(f"destination already exists: {destination}")
    destination.mkdir(parents=True, exist_ok=True)

    canbin.write(destination / "can" / f"bus_{selected_bus}.canbin",
                 selected_bus, frames)
    shutil.copytree(source_package / "dbc", destination / "dbc", dirs_exist_ok=True)
    _write_video(destination / "video.mp4", duration_sec=duration_sec)
    _write_thumbnail(destination / "video.mp4", destination / "thumbnail.jpg")

    stats = bus_statistics(selected_bus, frames)
    document = dict(source_manifest)
    document.update({
        "scenario_id": scenario_id,
        "title": f"RAV4 PCAN Stress {target_wire_bps // 1000}k",
        "dataset": "synthetic_stress",
        "route": f"stress:{source_manifest['scenario_id']}:bus{selected_bus}",
        "segment": 0,
        "duration_sec": round(duration_sec, 3),
        "video": "video.mp4",
        "video_fps": 30.0,
        "video_frame_count": round(duration_sec * 30),
        "thumbnail": "thumbnail.jpg",
        "available_buses": [selected_bus],
        "default_bus": selected_bus,
        "default_bus_reason": (
            f"high-load timeline generated from {source_manifest['scenario_id']} "
            f"bus {selected_bus}"),
        "original_bitrate": None,
        "playback_bitrate": playback_bitrate,
        "video_can_offset_ms": 0.0,
        "tags": ["Stress Test"],
        "tag_evidence": {},
        "description": (
            f"{duration_sec:g}-second Toyota RAV4 receiver stress test generated from "
            f"{source_manifest['scenario_id']} bus {selected_bus}. Frame order and "
            "payloads are preserved, but timing is synthetic and must not be used "
            "for ECU cycle-time validation."),
        "can": {str(selected_bus): f"bus_{selected_bus}.canbin"},
        "bus_statistics": [stats.as_dict()],
        "telemetry": None,
        "stress_test": {
            "source_scenario_id": source_manifest["scenario_id"],
            "source_bus": selected_bus,
            "target_wire_load_bps": target_wire_bps,
            "calculated_wire_load_bps": round(actual_wire_bps, 3),
            "playback_bitrate_bps": playback_bitrate,
            "load_fraction": round(target_wire_bps / playback_bitrate, 3),
            "wire_size_model": "ISO 11898-1 classical CAN with calculated bit stuffing",
            "tx_echo_flags_cleared": True,
        },
    })
    (destination / "scenario.json").write_text(
        json.dumps(document, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (destination / "validation.json").write_text(json.dumps({
        "format_version": 1,
        "status": "synthetic_stress_timeline",
        "frame_count": len(frames),
        "calculated_wire_load_bps": round(actual_wire_bps, 3),
        "note": "Payloads originate from the source RAV4 recording; timing is synthetic.",
    }, indent=2) + "\n", encoding="utf-8")
    (destination / "conversion.json").write_text(json.dumps({
        "format_version": 1,
        "generator": "scenario_builder stress",
        "source_package": str(source_package),
        "target_wire_load_bps": target_wire_bps,
        "duration_sec": duration_sec,
        "video": "FFmpeg SMPTE bars, 1280x720, 30 fps, H.264",
    }, indent=2) + "\n", encoding="utf-8")
    playlists.rebuild(output_root)
    return destination