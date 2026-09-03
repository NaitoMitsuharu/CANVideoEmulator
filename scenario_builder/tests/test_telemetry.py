import numpy as np

from scenario_builder import comma2k19, telemetry


def _write_series(folder, t, value):
    folder.mkdir(parents=True, exist_ok=True)
    np.save(folder / "t", np.asarray(t, dtype=np.float64), allow_pickle=False)
    np.save(folder / "value", np.asarray(value, dtype=np.float64), allow_pickle=False)
    # comma2k19 stores these without the .npy suffix; mirror that on disk.
    (folder / "t").write_bytes((folder / "t.npy").read_bytes())
    (folder / "value").write_bytes((folder / "value.npy").read_bytes())
    (folder / "t.npy").unlink()
    (folder / "value.npy").unlink()


def _segment(tmp_path):
    return comma2k19.Segment(tmp_path, "99c94dc769b5d96e", "2018-05-01--08-13-53", 25)


def test_build_telemetry_aligns_to_first_can_frame_and_converts_units(tmp_path):
    proc = tmp_path / "processed_log"
    # First CAN frame boot time = 1000.0 s -> t0. GNSS fixes at 1000..1002 s.
    _write_series(proc / "GNSS" / "live_gnss_ublox",
                  t=[1000.0, 1001.0, 1002.0],
                  value=[[37.5, -122.3, 10.0, 0, 5, 90],
                         [37.5001, -122.3, 20.0, 0, 5, 90],
                         [37.5002, -122.3, 30.0, 0, 5, 90]])
    # Magnetometer in tesla -> should surface as microtesla.
    _write_series(proc / "IMU" / "magnetometer",
                  t=[1000.0, 1000.5, 1001.0],
                  value=[[1e-5, 2e-5, 3e-5]] * 3)

    doc = telemetry.build_telemetry(_segment(tmp_path), first_frame_mono_ns=1000 * 10**9)

    assert doc is not None
    assert doc["gnss"]["source"] == "live_gnss_ublox"
    assert doc["gnss"]["t"] == [0.0, 1.0, 2.0]              # rebased to t0
    assert doc["gnss"]["speed_kmh"] == [36.0, 72.0, 108.0]  # m/s -> km/h
    # North increases as latitude increases; east ~0 for constant longitude.
    assert doc["gnss"]["north_m"][2] > doc["gnss"]["north_m"][0]
    assert abs(doc["gnss"]["east_m"][1]) < 1.0
    mag = doc["imu"]["magnetometer"]
    assert mag["unit"] == "uT"
    assert mag["x"][0] == 10.0 and mag["y"][0] == 20.0 and mag["z"][0] == 30.0


def test_imu_is_decimated_to_target_rate(tmp_path):
    proc = tmp_path / "processed_log"
    # 1000 Hz accelerometer over 1 s -> 1001 samples; expect ~26 at 25 Hz.
    t = np.linspace(0.0, 1.0, 1001)
    value = np.column_stack([np.sin(t), np.cos(t), t])
    _write_series(proc / "IMU" / "accelerometer", t=t, value=value)

    doc = telemetry.build_telemetry(_segment(tmp_path), first_frame_mono_ns=0,
                                    imu_hz=25.0)

    accel = doc["imu"]["accelerometer"]
    assert 20 <= len(accel["t"]) <= 30
    assert accel["t"][0] == 0.0 and accel["t"][-1] == 1.0  # endpoints kept


def test_build_telemetry_returns_none_without_gnss_or_imu(tmp_path):
    warnings: list[str] = []
    assert telemetry.build_telemetry(_segment(tmp_path), 0, warnings=warnings) is None
    assert any("GNSS" in w for w in warnings)
    assert any("IMU" in w for w in warnings)


def test_ensure_for_package_tops_up_missing_telemetry_without_reencoding(tmp_path):
    import json

    # A source segment with GNSS + frame_times, and t0 reconstructable from the
    # package's recorded video offset: video_can_offset_ms = (frame_times[0]-t0)*1000.
    source = tmp_path / "source"
    proc = source / "processed_log"
    _write_series(proc / "GNSS" / "live_gnss_ublox",
                  t=[1000.0, 1001.0],
                  value=[[37.5, -122.3, 10.0, 0, 5, 90], [37.5, -122.3, 12.0, 0, 5, 90]])
    (source / "global_pose").mkdir(parents=True)
    np.save(source / "global_pose" / "frame_times", np.array([1000.05]), allow_pickle=False)
    (source / "global_pose" / "frame_times").write_bytes(
        (source / "global_pose" / "frame_times.npy").read_bytes())
    (source / "global_pose" / "frame_times.npy").unlink()

    package = tmp_path / "pkg"
    package.mkdir()
    # offset so that t0 = frame_times[0] - offset/1000 = 1000.05 - 0.05 = 1000.0 s.
    (package / "scenario.json").write_text(
        json.dumps({"scenario_id": "x", "video_can_offset_ms": 50.0}), encoding="utf-8")

    segment = comma2k19.Segment(source, "99c94dc769b5d96e", "2018-05-01--08-13-53", 25)
    assert telemetry.ensure_for_package(package, segment) is True

    doc = json.loads((package / "telemetry.json").read_text(encoding="utf-8"))
    assert doc["gnss"]["t"] == [0.0, 1.0]                      # rebased to t0=1000 s
    manifest = json.loads((package / "scenario.json").read_text(encoding="utf-8"))
    assert manifest["telemetry"] == "telemetry.json"

    # Idempotent: a second call finds the sidecar already present.
    assert telemetry.ensure_for_package(package, segment) is False
