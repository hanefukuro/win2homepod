// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Discovery;

namespace WinPlay.Core.Raop;

/// <summary>Allowlisted comparison data, excluding receiver identity and key material.</summary>
public sealed record ReceiverCapabilitySnapshot
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "model", "manufacturer", "sourceVersion", "firmwareRevision", "protocolVersion", "build",
        "features", "statusFlags", "audioFormats", "audioFormat", "audioModes", "audioMode",
        "audioLatencies", "audioLatency", "latency", "latencyMin", "latencyMax", "latencyMs",
        "arrivalToRenderLatencyMs", "inputLatencyMicros", "outputLatencyMicros",
        "type", "ct", "spf", "sr", "sampleRate", "inputFormats", "outputFormats",
        "audioInputFormats", "audioOutputFormats", "channels", "bitDepth", "streams",
        "supportsDynamicStreamID", "supportsBufferedAudio", "supportsPTP",
    };
    private static readonly HashSet<string> TxtAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "features", "ft", "model", "am", "srcvers", "sf", "cn", "ch", "ss", "sr", "et", "tp", "vn", "vs", "vv",
    };

    public Dictionary<string, object?> GetInfo { get; init; } = [];
    public string? InfoFeaturesHex { get; init; }
    public string? MdnsFeaturesHex { get; init; }
    public string MdnsStatus { get; init; } = "not-captured";
    public Dictionary<string, string> AirPlayTxt { get; init; } = [];
    public Dictionary<string, string> RaopTxt { get; init; } = [];

    public static ReceiverCapabilitySnapshot Capture(Dictionary<string, object?> info, AirPlayDevice? device = null) => new()
    {
        GetInfo = Filter(info),
        InfoFeaturesHex = info.GetValueOrDefault("features") is long bits ? $"0x{unchecked((ulong)bits):X16}" : null,
        MdnsFeaturesHex = device is null ? null : $"0x{device.RawFeatures:X16}",
        MdnsStatus = device is null ? "not-captured" : "captured",
        AirPlayTxt = FilterTxt(device?.AirPlayTxt), RaopTxt = FilterTxt(device?.RaopTxt),
    };

    public static Dictionary<string, string> FilterTxt(IReadOnlyDictionary<string, string>? txt) =>
        txt?.Where(kv => TxtAllowed.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];

    public static Dictionary<string, object?> Filter(Dictionary<string, object?> dictionary) =>
        dictionary.Where(kv => Allowed.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => FilterValue(kv.Value));

    private static object? FilterValue(object? value) => value switch
    {
        Dictionary<string, object?> d => Filter(d),
        List<object?> l => l.Select(FilterValue).ToList(),
        long or int or bool => value,
        double d when double.IsFinite(d) => d,
        string s => s.Length <= 256 ? s : s[..256],
        _ => null, // Binary keys/TXT blobs and unsupported types are never persisted.
    };
}
