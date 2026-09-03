"""Download the pinned small example, or extract a chunk on Windows (stdlib only)."""
import argparse
import json
import shutil
import time
import urllib.request
import zipfile
from pathlib import Path, PurePosixPath


def safe_target(root: Path, name: str) -> Path:
    parts = PurePosixPath(name.replace("\\", "/")).parts
    if not parts or any(p in ("..", "/") or ":" in p for p in parts):
        raise ValueError(f"Unsafe archive path: {name}")
    target = root.joinpath(*(p.replace("|", "_") for p in parts)).resolve()
    if not target.is_relative_to(root.resolve()):
        raise ValueError(f"Path escapes destination: {name}")
    return target


def download_example(root: Path) -> Path:
    manifest = Path(__file__).resolve().parents[1] / "assets/comma2k19-example.json"
    for entry in json.loads(manifest.read_text(encoding="utf-8")):
        path = safe_target(root, entry["path"])
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.is_file() and path.stat().st_size == entry["size"]:
            continue
        partial = path.with_name(path.name + ".partial")
        print(f"Download {path.name}: {entry['size'] / 1e6:.2f} MB", flush=True)
        with urllib.request.urlopen(entry["url"], timeout=90) as response, partial.open("wb") as output:
            shutil.copyfileobj(response, output)
        if partial.stat().st_size != entry["size"]:
            raise ValueError(f"Incomplete download: {path}")
        partial.replace(path)
    return root / "Example_1/b0c9d2329ad1606b_2018-08-02--08-34-47/40"


def extract(archive: Path, root: Path) -> None:
    with zipfile.ZipFile(archive) as source:
        # Validate every path before writing any files.
        targets = [(info, safe_target(root, info.filename)) for info in source.infolist()]
        total = sum(info.file_size for info, _ in targets if not info.is_dir())
        completed = 0
        last_report = 0.0
        def progress(force=False):
            nonlocal last_report
            now = time.monotonic()
            if force or now - last_report >= 0.25:
                print("CANVIDEO_PROGRESS " + json.dumps(dict(phase="extract", completed=completed,
                    total=max(1, total), detail=archive.name)), flush=True)
                last_report = now
        progress(True)
        for info, target in targets:
            if info.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            if target.is_file() and target.stat().st_size == info.file_size:
                completed += info.file_size
                progress()
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            partial = target.with_name(target.name + ".extracting")
            with source.open(info) as data, partial.open("wb") as output:
                while block := data.read(1024 * 1024):
                    output.write(block)
                    completed += len(block)
                    progress()
            partial.replace(target)  # ZipFile verifies CRC while reading.
        progress(True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--archive", type=Path)
    args = parser.parse_args()
    if args.archive:
        extract(args.archive, args.output)
    else:
        print(download_example(args.output))
