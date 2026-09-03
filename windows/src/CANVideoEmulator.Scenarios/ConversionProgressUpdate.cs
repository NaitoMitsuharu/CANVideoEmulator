using System.Text.Json;

namespace CANVideoEmulator.Scenarios;

/// <summary>Measured work within an import phase, not an estimate of time remaining.</summary>
public readonly record struct ConversionProgressUpdate(string Phase, long Completed, long Total, string Detail)
{
    public const string Prefix = "CANVIDEO_PROGRESS ";
    // Fixed phase weights keep the overall bar moving forward. Final success is
    // reported by the process exit, so even a full verification count is < 100%.
    public double Fraction => Phase switch
    {
        "prepare" => 0.05 * Completed / Total,
        "extract" => 0.05 + 0.15 * Completed / Total,
        "build" => 0.20 + 0.70 * Completed / Total,
        "verify" => 0.90 + 0.09 * Completed / Total,
        _ => 0,
    };

    public string PhaseText => Phase switch
    {
        "prepare" => "Preparing tools",
        "extract" when Total == 1 && Detail == "Input files ready" => Detail,
        "extract" => $"Extracting · {Completed / 1e6:0.0} / {Total / 1e6:0.0} MB",
        "build" => $"Converting · {Completed:N0} / {Total:N0} segments",
        "verify" => $"Verifying · {Completed:N0} / {Total:N0} scenarios",
        _ => Phase,
    };

    public static bool TryParse(string line, out ConversionProgressUpdate progress)
    {
        progress = default;
        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            using var document = JsonDocument.Parse(line[Prefix.Length..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("phase", out var phase) || phase.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.Number || !completed.TryGetInt64(out var done) ||
                !root.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out var count) ||
                count <= 0 || done < 0 || done > count) return false;
            var name = phase.GetString()!;
            if (name is not ("prepare" or "extract" or "build" or "verify")) return false;
            var detail = root.TryGetProperty("detail", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
            progress = new(name, done, count, detail);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
