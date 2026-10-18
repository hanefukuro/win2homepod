using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using WinPlay.Core.Discovery;
using WinPlay.Core.Plist;
using WinPlay.Core.Raop;
using WinPlay.Core.Rtsp;

namespace Win2HomePod.Probe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try { return await ProbeCommand.RunAsync(args, Console.Out, Console.Error); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException
            or IOException or UnauthorizedAccessException or JsonException)
        {
            // Do not print raw exception messages which can include paths or response bodies.
            Console.Error.WriteLine($"Invalid command/input or output unavailable ({ex.GetType().Name}). Use --help.");
            return 2;
        }
    }
}

internal sealed record ParsedCommand(string Command, Dictionary<string, string> Values, StreamProbeOptions Stream)
{
    public string Get(string key, string fallback) => Values.GetValueOrDefault(key) ?? fallback;
}

internal static class ProbeCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> Common = ["--output"];
    private static readonly HashSet<string> StreamFlags = ["--stream-type", "--codec", "--audio-format", "--spf", "--sample-rate", "--audio-mode", "--latency-min", "--latency-max", "--timing"];

    internal static ParsedCommand Parse(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("command required");
        string command = args[0];
        var allowed = new HashSet<string>(Common);
        switch (command)
        {
            case "plan": allowed.UnionWith(StreamFlags); break;
            case "probe": allowed.UnionWith(StreamFlags); goto case "capture";
            case "capture": allowed.UnionWith(["--host", "--port", "--timeout-seconds", "--discover-ms"]); break;
            case "import-info": allowed.UnionWith(["--input", "--txt-json"]); break;
            case "compare": allowed.UnionWith(["--left", "--right"]); break;
            default: throw new ArgumentException("unknown command");
        }
        var values = new Dictionary<string, string>();
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !allowed.Contains(args[i]) || args[i + 1].StartsWith("--")
                || !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("unknown, duplicated or incomplete option");
        }
        long Number(string key, long fallback)
        {
            if (!values.TryGetValue(key, out string? value)) return fallback;
            bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!long.TryParse(hex ? value[2..] : value, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long parsed)) throw new ArgumentException("numeric option required");
            return parsed;
        }
        string timing = values.GetValueOrDefault("--timing") ?? "ptp";
        if (timing is not ("ptp" or "ntp")) throw new ArgumentException("timing must be ptp or ntp");
        var stream = new StreamProbeOptions
        {
            StreamType = Number("--stream-type", 0x60), Codec = Number("--codec", 2),
            AudioFormat = Number("--audio-format", 0x40000), SamplesPerFrame = checked((int)Number("--spf", 352)),
            SampleRate = checked((int)Number("--sample-rate", 44100)), AudioMode = values.GetValueOrDefault("--audio-mode") ?? "default",
            LatencyMinFrames = checked((int)Number("--latency-min", 6615)),
            LatencyMaxFrames = checked((int)Number("--latency-max", 6615)), UsePtp = timing == "ptp",
        };
        stream.Validate();
        if (command is "probe" or "capture")
        {
            if (!IPAddress.TryParse(values.GetValueOrDefault("--host"), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("--host requires an IPv4 address");
            int Decimal(string key, string fallback) => int.Parse(values.GetValueOrDefault(key) ?? fallback, CultureInfo.InvariantCulture);
            if (Decimal("--port", "7000") is < 1 or > 65535 || Decimal("--timeout-seconds", "45") is < 1 or > 120
                || Decimal("--discover-ms", "2000") is < 0 or > 10000) throw new ArgumentException("invalid port or timeout");
        }
        foreach (string required in command switch
        {
            "import-info" => new[] { "--input" }, "compare" => ["--left", "--right"], _ => Array.Empty<string>(),
        })
            if (!values.ContainsKey(required)) throw new ArgumentException("missing input option");
        // All output files are explicit. Stdout remains available for dry-run/CI use.
        if (command is "probe" or "capture" && !values.ContainsKey("--output")) throw new ArgumentException("--output required for live operations");
        return new(command, values, stream);
    }

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            await stdout.WriteLineAsync("""
                Win2HomePod.Probe: plan | capture | probe | import-info | compare
                plan/probe: --stream-type 0x60 --codec 2 --audio-format 0x40000 --spf 352
                  --sample-rate 44100 --audio-mode default --latency-min 6615 --latency-max 6615 --timing ptp|ntp
                Latency values are sample frames; codec/audio-format are raw observed numeric values.
                capture/probe (Windows): --host IPv4 --port 7000 --timeout-seconds 45 --discover-ms 2000 --output FILE
                import-info: --input decrypted-info.bplist [--txt-json txt.json] [--output FILE]
                compare: --left report.json --right report.json [--output FILE]
                plan sends no traffic; probe negotiates SETUP then TEARDOWN, never sends audio.
                SETUP acceptance does not prove audible output or lower latency.
                """);
            return 0;
        }
        var command = Parse(args);
        if (command.Values.TryGetValue("--output", out string? existingOutput) && File.Exists(existingOutput))
            throw new IOException("output already exists");
        object result;
        int exitCode = 0;
        switch (command.Command)
        {
            case "plan":
                result = new { SchemaVersion = 1, Mode = "offline-plan", Requested = command.Stream,
                    StreamDeclaration = command.Stream.ToDeclaration(), AudioPacketsSent = 0, MeasuredOutputLatencyMs = (double?)null };
                break;
            case "import-info":
                var snapshot = ReceiverCapabilitySnapshot.Capture(BinaryPlist.ReadDictionary(await File.ReadAllBytesAsync(command.Values["--input"])));
                if (command.Values.TryGetValue("--txt-json", out var path))
                {
                    var txt = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(await File.ReadAllTextAsync(path))
                        ?? throw new JsonException("TXT object required");
                    snapshot = snapshot with { AirPlayTxt = ReceiverCapabilitySnapshot.FilterTxt(txt.GetValueOrDefault("airplay")),
                        RaopTxt = ReceiverCapabilitySnapshot.FilterTxt(txt.GetValueOrDefault("raop")), MdnsStatus = "imported" };
                }
                result = new { SchemaVersion = 1, Mode = "imported-info", Capabilities = snapshot };
                break;
            case "compare":
                using (var left = JsonDocument.Parse(await File.ReadAllTextAsync(command.Values["--left"])))
                using (var right = JsonDocument.Parse(await File.ReadAllTextAsync(command.Values["--right"])))
                    result = Compare(left.RootElement, right.RootElement);
                break;
            default:
                if (!OperatingSystem.IsWindows()) throw new ArgumentException("live commands require Windows; offline commands are portable");
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(command.Get("--timeout-seconds", "45"), CultureInfo.InvariantCulture))))
                {
                    var host = IPAddress.Parse(command.Values["--host"]);
                    int port = int.Parse(command.Get("--port", "7000"), CultureInfo.InvariantCulture);
                    StreamProbeReport report;
                    if (command.Command == "probe") report = await RaopSession.ProbeAsync(host, port, command.Stream, timeout.Token);
                    else report = await CaptureAsync(host, port, timeout.Token);
                    if (!timeout.IsCancellationRequested)
                        report.Capabilities = await AddMdnsAsync(report.Capabilities, host, int.Parse(command.Get("--discover-ms", "2000"), CultureInfo.InvariantCulture), timeout.Token);
                    result = report;
                    exitCode = report.Outcome is "setup-accepted" or "capabilities-captured" ? 0 : 1;
                }
                break;
        }
        string json = JsonSerializer.Serialize(result, JsonOptions);
        if (command.Values.TryGetValue("--output", out string? output))
        {
            string fullPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            // Never overwrite an earlier experiment or an input file by accident.
            await using var file = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write);
            await using var writer = new StreamWriter(file);
            await writer.WriteLineAsync(json);
            await stderr.WriteLineAsync("Report saved. This report does not measure output latency.");
        }
        else await stdout.WriteLineAsync(json);
        return exitCode;
    }

    internal static async Task<StreamProbeReport> CaptureAsync(IPAddress host, int port, CancellationToken ct)
    {
        var report = new StreamProbeReport { Mode = "capability-capture", Phase = "info" };
        using var connection = new RtspConnection();
        try
        {
            await connection.ConnectAsync(host, port, ct);
            var response = await connection.RequestAsync(new() { Method = "GET", Uri = "/info" }, ct);
            report.Observe("info", "GET", response);
            report.Outcome = response.StatusCode != 200 ? "rejected" : report.Capabilities is null ? "protocol-error" : "capabilities-captured";
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or RtspException)
        {
            report.Outcome = ex is OperationCanceledException ? "cancelled-or-timeout" : "error";
            report.ErrorKind = ex.GetType().Name;
        }
        return report;
    }

    private static async Task<ReceiverCapabilitySnapshot?> AddMdnsAsync(ReceiverCapabilitySnapshot? info, IPAddress host, int milliseconds, CancellationToken ct)
    {
        if (info is null || milliseconds == 0) return info;
        try
        {
            using var browser = new AirPlayBrowser();
            browser.Start();
            await Task.Delay(milliseconds, ct);
            var device = browser.Snapshot().FirstOrDefault(d => d.Addresses.Contains(host));
            if (device is null) return info with { MdnsStatus = "not-found" };
            return info with { AirPlayTxt = ReceiverCapabilitySnapshot.FilterTxt(device.AirPlayTxt),
                RaopTxt = ReceiverCapabilitySnapshot.FilterTxt(device.RaopTxt), MdnsFeaturesHex = $"0x{device.RawFeatures:X16}", MdnsStatus = "captured" };
        }
        catch (OperationCanceledException) { return info with { MdnsStatus = "timeout" }; }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        { return info with { MdnsStatus = "unavailable" }; }
    }

    internal static object Compare(JsonElement left, JsonElement right)
    {
        // Compare only evidence-bearing fields, ignoring file paths and experiment identity.
        var differences = new List<object>();
        foreach (string name in new[] { "Requested", "Capabilities", "Outcome", "Responses" })
        {
            string a = left.TryGetProperty(name, out var av) ? Canonical(av) : "null";
            string b = right.TryGetProperty(name, out var bv) ? Canonical(bv) : "null";
            if (a != b) differences.Add(new { Field = name, Left = JsonSerializer.Deserialize<JsonElement>(a), Right = JsonSerializer.Deserialize<JsonElement>(b) });
        }
        return new { Mode = "offline-comparison", Differences = differences, Conclusion = "No output-latency conclusion; external measurements required." };
    }

    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        _ => element.GetRawText(),
    };
}
