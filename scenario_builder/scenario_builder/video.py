"""Video conversion for scenario packages (requirement 13).

comma2k19 ships a raw HEVC elementary stream (``video.hevc``): no container, no
timestamps, and no seek index.  WPF's MediaElement cannot seek that reliably even
where an HEVC decoder is installed, and requiring an HEVC codec on the exhibition
PC would break the "copy it to another Windows box and run" goal.

So the builder transcodes once, here, to H.264 in MP4:

* ``-c:v libx264`` -- decodable by the H.264 decoder that ships with Windows 10
  and 11, so the player needs no extra codec install.
* ``-g <fps>`` with ``-keyint_min <fps>`` -- a keyframe every second, which is
  what makes seeking land quickly and accurately.  Requirement 10's seek path is
  only as good as the keyframe spacing.
* ``-movflags +faststart`` -- moov atom at the front so the player can start
  without reading the whole file.
* ``-r <fps>`` with ``-fps_mode cfr`` -- a constant frame rate, so a position in
  seconds maps to a frame without a timestamp table.

FFmpeg is a build-time dependency only.  The Windows player never invokes it.
"""

from __future__ import annotations

import json
import shutil
import subprocess
from dataclasses import dataclass, asdict
from pathlib import Path

# comma2k19's camera runs at 20 Hz; the dataset README and the 1200 frame /
# 60 second segment length both agree.  Overridable, never inferred silently.
DEFAULT_FPS = 20.0


class FfmpegMissingError(RuntimeError):
    pass


class VideoConversionError(RuntimeError):
    pass


@dataclass(slots=True)
class ConversionResult:
    source: str
    output: str
    thumbnail: str | None
    codec: str
    container: str
    fps: float
    frame_count: int | None
    duration_sec: float | None
    width: int | None
    height: int | None
    crf: int
    preset: str
    keyframe_interval_frames: int
    ffmpeg_version: str
    command: list[str]

    def as_dict(self) -> dict:
        return asdict(self)


def _tool(name: str) -> str:
    path = shutil.which(name)
    if path is None:
        raise FfmpegMissingError(
            f"{name} was not found on PATH. The Scenario Builder needs FFmpeg to "
            f"convert video.hevc; install it from https://ffmpeg.org/download.html. "
            f"(The Windows player itself does not need FFmpeg.)")
    return path


def ffmpeg_version() -> str:
    out = subprocess.run([_tool("ffmpeg"), "-version"], capture_output=True,
                         text=True, check=True)
    return out.stdout.splitlines()[0].strip() if out.stdout else "unknown"


def probe(path: Path) -> dict:
    """ffprobe a produced MP4 for the numbers we record in conversion.json."""
    result = subprocess.run(
        [_tool("ffprobe"), "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height,nb_frames,r_frame_rate,codec_name",
         "-show_entries", "format=duration",
         "-of", "json", str(path)],
        capture_output=True, text=True, check=True)
    return json.loads(result.stdout)


def convert(source_hevc: Path, output_mp4: Path, *, fps: float = DEFAULT_FPS,
            crf: int = 20, preset: str = "medium",
            overwrite: bool = True) -> ConversionResult:
    """Transcode a raw HEVC stream into a seekable H.264 MP4."""
    if not source_hevc.is_file():
        raise VideoConversionError(f"missing source video: {source_hevc}")
    output_mp4.parent.mkdir(parents=True, exist_ok=True)

    keyint = max(1, int(round(fps)))
    command = [
        _tool("ffmpeg"),
        "-y" if overwrite else "-n",
        "-hide_banner", "-loglevel", "error",
        # The source is a bare elementary stream, so the input frame rate has to
        # be stated: there are no container timestamps to read it from.
        "-fflags", "+genpts",
        "-r", f"{fps}",
        "-f", "hevc",
        "-i", str(source_hevc),
        "-an",
        "-c:v", "libx264",
        "-preset", preset,
        "-crf", str(crf),
        "-pix_fmt", "yuv420p",
        "-g", str(keyint),
        "-keyint_min", str(keyint),
        "-sc_threshold", "0",
        "-r", f"{fps}",
        "-fps_mode", "cfr",
        "-movflags", "+faststart",
        str(output_mp4),
    ]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0 or not output_mp4.is_file():
        raise VideoConversionError(
            f"ffmpeg failed converting {source_hevc.name}:\n{result.stderr.strip()}")

    info = probe(output_mp4)
    stream = (info.get("streams") or [{}])[0]
    duration = info.get("format", {}).get("duration")
    frame_count = stream.get("nb_frames")

    return ConversionResult(
        source=source_hevc.name,
        output=output_mp4.name,
        thumbnail=None,
        codec=stream.get("codec_name", "h264"),
        container="mp4",
        fps=fps,
        frame_count=int(frame_count) if frame_count not in (None, "N/A") else None,
        duration_sec=round(float(duration), 6) if duration else None,
        width=stream.get("width"),
        height=stream.get("height"),
        crf=crf,
        preset=preset,
        keyframe_interval_frames=keyint,
        ffmpeg_version=ffmpeg_version(),
        command=[Path(command[0]).name, *command[1:]],
    )


def make_thumbnail(segment_preview: Path | None, video_mp4: Path,
                   output_jpg: Path, *, at_seconds: float = 2.0,
                   width: int = 640) -> Path:
    """Produce the browser card thumbnail (requirement 34).

    comma2k19 gives every segment a ``preview.png`` of its first frame; prefer it
    so the card matches the dataset, and fall back to grabbing a frame from the
    converted video.
    """
    output_jpg.parent.mkdir(parents=True, exist_ok=True)

    if segment_preview is not None and segment_preview.is_file():
        command = [_tool("ffmpeg"), "-y", "-hide_banner", "-loglevel", "error",
                   "-i", str(segment_preview),
                   "-vf", f"scale={width}:-2", "-q:v", "3", str(output_jpg)]
    else:
        command = [_tool("ffmpeg"), "-y", "-hide_banner", "-loglevel", "error",
                   "-ss", f"{at_seconds}", "-i", str(video_mp4),
                   "-frames:v", "1", "-vf", f"scale={width}:-2",
                   "-q:v", "3", str(output_jpg)]

    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0 or not output_jpg.is_file():
        raise VideoConversionError(
            f"ffmpeg failed making thumbnail {output_jpg.name}:\n{result.stderr.strip()}")
    return output_jpg
