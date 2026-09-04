using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CANVideoEmulator.Core.Can;
using CANVideoEmulator.Scenarios;

namespace CANVideoEmulator.Wpf.Converters;

/// <summary>Colours the PCAN status pill by health (requirement 20).</summary>
public sealed class HealthToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Good = Frozen("#3FB950");
    private static readonly SolidColorBrush Warn = Frozen("#D29922");
    private static readonly SolidColorBrush Bad = Frozen("#F85149");
    private static readonly SolidColorBrush Idle = Frozen("#6E7681");

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CanTransportHealth health
            ? health switch
            {
                CanTransportHealth.Ok => Good,
                CanTransportHealth.BusLight or CanTransportHealth.BusHeavy => Warn,
                CanTransportHealth.BusOff or CanTransportHealth.BusPassive or CanTransportHealth.Error => Bad,
                CanTransportHealth.DriverMissing or CanTransportHealth.ApiMissing
                    or CanTransportHealth.ChannelInUse => Warn,
                _ => Idle,
            }
            : Idle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound value is null -- used for empty-state text.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound value is a non-empty string or a non-zero count.</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible,
            int count => count > 0 ? Visibility.Visible : Visibility.Collapsed,
            null => Visibility.Collapsed,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Friendly names for <see cref="Scenarios.LoopMode"/> in the toolbar.</summary>
public sealed class LoopModeNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            Scenarios.LoopMode.PlaylistAdvance => "Next in playlist",
            Scenarios.LoopMode.PlaylistLoop => "Loop playlist",
            Scenarios.LoopMode.SingleScenarioLoop => "Loop this scenario",
            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when a bound double is at or above the threshold in <c>ConverterParameter</c>.</summary>
/// <remarks>Used to colour the bus-load bar without a code-behind event handler.</remarks>
public sealed class ThresholdToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double actual && parameter is string text &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) &&
        actual >= threshold;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Bit rates as "500 kbit/s" rather than "500,000 bit/s".
/// </summary>
/// <remarks>
/// CAN rates are universally spoken and written in kbit/s, and the raw figure
/// takes a moment to read in a dropdown. 83333 and 47619 are the two rates PEAK
/// offers that are not whole thousands, so they keep one decimal.
/// </remarks>
public sealed class BitrateTextConverter : IValueConverter
{
    public static string Format(int bitsPerSecond)
    {
        if (bitsPerSecond >= 1_000_000 && bitsPerSecond % 1_000_000 == 0)
        {
            return $"{bitsPerSecond / 1_000_000} Mbit/s";
        }

        return bitsPerSecond % 1_000 == 0
            ? $"{bitsPerSecond / 1_000} kbit/s"
            : $"{bitsPerSecond / 1000.0:0.#} kbit/s";
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int bits ? Format(bits) : value?.ToString() ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
