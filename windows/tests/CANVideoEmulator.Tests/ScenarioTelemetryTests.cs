using CANVideoEmulator.Scenarios;
using Xunit;

namespace CANVideoEmulator.Tests;

public class ScenarioTelemetryTests
{
    private static string WriteTelemetry(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetry_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ParsesGnssAndImuInDisplayOrder()
    {
        var path = WriteTelemetry("""
        {
          "format_version": 1,
          "gnss": { "t": [0, 1, 2], "speed_kmh": [10, 20, 30],
                    "east_m": [0, 5, 10], "north_m": [0, 0, 0] },
          "imu": {
            "magnetometer":  { "unit": "uT",    "t": [0, 1], "x": [1, 1], "y": [2, 2], "z": [3, 3] },
            "accelerometer": { "unit": "m/s^2", "t": [0, 1], "x": [0, 1], "y": [0, 1], "z": [0, 1] },
            "gyro":          { "unit": "rad/s", "t": [0, 1], "x": [0, 0], "y": [0, 0], "z": [0, 0] }
          }
        }
        """);
        try
        {
            var telemetry = ScenarioTelemetry.Load(path);

            Assert.True(telemetry.HasGnss);
            Assert.True(telemetry.HasImu);
            // Always accel, gyro, mag regardless of JSON order.
            Assert.Equal(["ACCEL", "GYRO", "MAG"], telemetry.Imu.Select(s => s.Label));
            Assert.Equal("m/s^2", telemetry.Imu[0].Unit);
            Assert.Equal(3, telemetry.Gnss!.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SpeedIsLinearlyInterpolatedAndClampedToEnds()
    {
        var path = WriteTelemetry("""
        { "gnss": { "t": [0, 2, 4], "speed_kmh": [0, 40, 80],
                    "east_m": [0, 0, 0], "north_m": [0, 0, 0] } }
        """);
        try
        {
            var gnss = ScenarioTelemetry.Load(path).Gnss!;

            Assert.Equal(20, gnss.SpeedAt(1));      // halfway between 0 and 40
            Assert.Equal(60, gnss.SpeedAt(3));      // halfway between 40 and 80
            Assert.Equal(0, gnss.SpeedAt(-5));      // clamped to first
            Assert.Equal(80, gnss.SpeedAt(100));    // clamped to last
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LatLonAreInterpolatedWhenPresentAndNaNWhenMissing()
    {
        var withLatLon = WriteTelemetry("""
        { "gnss": { "t": [0, 2], "speed_kmh": [0, 0], "east_m": [0, 0], "north_m": [0, 0],
                    "lat": [35.0, 35.002], "lon": [139.0, 139.001] } }
        """);
        var withoutLatLon = WriteTelemetry("""
        { "gnss": { "t": [0, 2], "speed_kmh": [0, 0], "east_m": [0, 0], "north_m": [0, 0] } }
        """);
        try
        {
            var withCoords = ScenarioTelemetry.Load(withLatLon).Gnss!;
            Assert.True(withCoords.HasLatLon);
            Assert.Equal(35.001, withCoords.LatAt(1), 6);
            Assert.Equal(139.0005, withCoords.LonAt(1), 6);

            var withoutCoords = ScenarioTelemetry.Load(withoutLatLon).Gnss!;
            Assert.False(withoutCoords.HasLatLon);
            Assert.True(double.IsNaN(withoutCoords.LatAt(1)));
            Assert.True(double.IsNaN(withoutCoords.LonAt(1)));
        }
        finally
        {
            File.Delete(withLatLon);
            File.Delete(withoutLatLon);
        }
    }

    [Fact]
    public void MissingGnssOrImuJustLeavesThatHalfEmpty()
    {
        var path = WriteTelemetry("""{ "format_version": 1 }""");
        try
        {
            var telemetry = ScenarioTelemetry.Load(path);
            Assert.False(telemetry.HasAny);
            Assert.Null(telemetry.Gnss);
            Assert.Empty(telemetry.Imu);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LowerBoundFindsFirstIndexAtOrAfterTarget()
    {
        double[] t = [0, 1, 2, 3, 4];
        Assert.Equal(0, ScenarioTelemetry.LowerBound(t, -1));
        Assert.Equal(2, ScenarioTelemetry.LowerBound(t, 2));
        Assert.Equal(3, ScenarioTelemetry.LowerBound(t, 2.5));
        Assert.Equal(5, ScenarioTelemetry.LowerBound(t, 9));
    }
}
