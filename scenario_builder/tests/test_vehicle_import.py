import json

from scenario_builder import builder, cli, comma2k19
from scenario_builder.canbin import CanFrame


def test_civic_auto_selection_uses_honda_files_and_preserves_explicit_profile(tmp_path):
    for name in cli.DEFAULT_CIVIC_DBC:
        (tmp_path / name).touch()
    config = builder.BuilderConfig(tmp_path / "out", [], cli.DEFAULT_RAV4_PROFILE)
    segment = comma2k19.Segment(tmp_path, "99c94dc769b5d96e", "2018-05-01--08-13-53", 25)
    chosen = cli.config_for_vehicle(config, segment, tmp_path, True)
    assert chosen.dbc_profile == "honda_civic_2016"
    assert [p.name for p in chosen.dbc_paths] == cli.DEFAULT_CIVIC_DBC
    assert chosen.candidate_dbc_paths == {}
    assert chosen.scenario_prefix == "civic"
    assert cli.config_for_vehicle(config, segment, tmp_path, False) is config


def test_import_only_reuses_a_complete_package_from_the_same_source_and_profile(tmp_path):
    segment = comma2k19.Segment(tmp_path, "99c94dc769b5d96e", "2018-05-01--08-13-53", 25)
    (tmp_path / "can").mkdir()
    (tmp_path / "dbc").mkdir()
    document = dict(route=segment.route, segment=25, dbc_profile="honda_civic_2016",
                    video="video.mp4", can={"0": "bus_0.canbin"})
    (tmp_path / "scenario.json").write_text(json.dumps(document), encoding="utf-8")
    assert not cli.reusable_package(tmp_path, segment, "honda_civic_2016")
    for name in ["video.mp4", "can/bus_0.canbin", "dbc/signals.json"]:
        (tmp_path / name).write_bytes(b"fixture")
    assert cli.reusable_package(tmp_path, segment, "honda_civic_2016")
    assert not cli.reusable_package(tmp_path, segment, "toyota_rav4_2017")
    segment.segment_index = 26
    assert not cli.reusable_package(tmp_path, segment, "honda_civic_2016")


def test_many_radar_ids_do_not_change_the_vehicle_bus_default(tmp_path, monkeypatch):
    primary = tmp_path / "vehicle.dbc"
    radar = tmp_path / "radar.dbc"
    primary.write_text('BO_ 170 VEHICLE_SPEED: 1 ECU\n SG_ SPEED : 0|8@1+ (1,0) [0|255] "km/h" ECU\n')
    radar.write_text("\n".join(f'BO_ {i} TARGET_{i}: 1 ECU\n SG_ DISTANCE : 0|8@1+ (1,0) [0|255] "m" ECU' for i in range(200, 206)))
    raw = comma2k19.RawLogCan(buses={
        0: [CanFrame(0, 170, 1, b"\x10")],
        1: [CanFrame(0, i, 1, b"\x10") for i in range(200, 206)],
    }, duration_us=1_000_000, first_frame_mono_ns=1_000_000_000)
    monkeypatch.setattr(comma2k19, "read_raw_log_can", lambda *a, **kw: raw)
    monkeypatch.setattr(comma2k19, "read_processed_can", lambda *a, **kw: {})
    segment = comma2k19.Segment(tmp_path / "source", "99c94dc769b5d96e", "2018-05-01--08-13-53", 25)
    result = builder.build_segment(segment, builder.BuilderConfig(tmp_path / "out", [primary, radar], "honda_civic_2016", skip_video=True), scenario_index=1)
    assert result.default_bus == 0
    assert result.frame_counts == {0: 1, 1: 6}
    manifest = json.loads((result.path / "scenario.json").read_text(encoding="utf-8"))
    assert manifest["vehicle"] == "Honda Civic"
