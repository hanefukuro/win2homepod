// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;

namespace WinPlay.Core.Raop;

/// <summary>A receiver field with its SETUP scope/path and unconverted numeric value.</summary>
public sealed record ReceiverLatencyField(string Path, object? Value, string ValueType);

/// <summary>
/// Latency-related SETUP fields. Units and semantics vary by receiver; these values
/// must not be presented as measured output latency or silently converted to ms.
/// Non-numeric payloads are represented by type only to avoid logging unrelated data.
/// </summary>
public sealed class ReceiverLatencyDiagnostics
{
    public int RequestedMilliseconds { get; }
    public int RequestedFrames { get; }
    public int SampleRate => AirPlayLatency.SampleRate;
    public double? MeasuredOutputLatencyMs => null; // Requires external acoustic measurement.
    public IReadOnlyList<ReceiverLatencyField> ReceiverFields { get; }

    private ReceiverLatencyDiagnostics(int requestedMilliseconds, List<ReceiverLatencyField> fields)
    {
        RequestedMilliseconds = requestedMilliseconds;
        RequestedFrames = AirPlayLatency.ToFrames(requestedMilliseconds);
        ReceiverFields = new ReadOnlyCollection<ReceiverLatencyField>(fields);
    }

    internal static ReceiverLatencyDiagnostics FromSetup(int requestedMilliseconds,
        Dictionary<string, object?> sessionSetup, Dictionary<string, object?>? streamSetup = null)
    {
        var fields = new List<ReceiverLatencyField>();
        Collect(sessionSetup, "session", false, fields);
        if (streamSetup is not null) Collect(streamSetup, "stream", false, fields);
        return new ReceiverLatencyDiagnostics(requestedMilliseconds, fields);
    }

    private static void Collect(object? value, string path, bool latencyField, List<ReceiverLatencyField> fields)
    {
        switch (value)
        {
            case Dictionary<string, object?> dictionary:
                foreach (var (key, child) in dictionary.OrderBy(p => p.Key, StringComparer.Ordinal))
                    Collect(child, $"{path}.{key}", latencyField
                        || key.Contains("latenc", StringComparison.OrdinalIgnoreCase)
                        || key.Contains("arrivalToRender", StringComparison.OrdinalIgnoreCase), fields);
                break;
            case List<object?> list:
                for (int i = 0; i < list.Count; i++) Collect(list[i], $"{path}[{i}]", latencyField, fields);
                break;
            default:
                if (!latencyField) break;
                object? number = value switch
                {
                    long or int => value,
                    double d when double.IsFinite(d) => d,
                    float f when float.IsFinite(f) => f,
                    _ => null,
                };
                fields.Add(new ReceiverLatencyField(path, number, value?.GetType().Name ?? "null"));
                break;
        }
    }

    public string ReceiverSummary => ReceiverFields.Count == 0 ? "応答なし"
        : string.Join(", ", ReceiverFields.Select(f => $"{f.Path}="
            + (Convert.ToString(f.Value, CultureInfo.InvariantCulture) is { Length: > 0 } text
                ? text : $"<{f.ValueType}>"))) + "（単位は受信機依存）";
}
