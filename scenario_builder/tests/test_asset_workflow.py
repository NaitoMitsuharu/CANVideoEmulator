import importlib.util
import json
from pathlib import Path
import zipfile

import pytest
from scenario_builder import playlists

script = Path(__file__).resolve().parents[2] / "scripts/dataset_assets.py"
spec = importlib.util.spec_from_file_location("dataset_assets", script)
assets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assets)


def test_windows_archive_paths_and_payload_are_preserved(tmp_path):
    archive = tmp_path / "chunk.zip"
    with zipfile.ZipFile(archive, "w") as output:
        output.writestr("Chunk_1/dongle|route/40/raw_log.bz2", b"original bytes")
    assets.extract(archive, tmp_path / "out")
    assert (tmp_path / "out/Chunk_1/dongle_route/40/raw_log.bz2").read_bytes() == b"original bytes"


@pytest.mark.parametrize("name", ["../escape", "/absolute", "C:/escape", "dir/../../escape", "..\\escape"])
def test_archive_cannot_escape_output_directory(tmp_path, name):
    with pytest.raises(ValueError):
        assets.safe_target(tmp_path, name)


def test_adding_a_chunk_keeps_previous_scenarios_in_all_playlist(tmp_path):
    for scenario_id in ["chunk1_001", "chunk2_001"]:
        folder = tmp_path / scenario_id
        folder.mkdir()
        (folder / "scenario.json").write_text(json.dumps({
            "scenario_id": scenario_id, "vehicle_model": "RAV4", "tags": [],
        }))
    playlists.rebuild(tmp_path)
    data = json.loads((tmp_path / "playlist.json").read_text())
    assert next(p for p in data["playlists"] if p["playlist_id"] == "all")["scenario_ids"] == ["chunk1_001", "chunk2_001"]


def test_extraction_progress_counts_reused_and_new_bytes(tmp_path, capsys):
    archive = tmp_path / "chunk.zip"
    with zipfile.ZipFile(archive, "w") as output:
        output.writestr("old", b"old")
        output.writestr("new", b"new data")
    target = tmp_path / "out"
    target.mkdir()
    (target / "old").write_bytes(b"old")
    before = (target / "old").stat().st_mtime_ns
    assets.extract(archive, target)
    reports = [json.loads(line.removeprefix("CANVIDEO_PROGRESS "))
               for line in capsys.readouterr().out.splitlines()
               if line.startswith("CANVIDEO_PROGRESS ")]
    assert reports[0]["completed"] == 0
    assert reports[-1]["completed"] == reports[-1]["total"] == 11
    assert all(report["phase"] == "extract" for report in reports)
    assert (target / "old").stat().st_mtime_ns == before
    assert (target / "new").read_bytes() == b"new data"
