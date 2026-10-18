// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Plist;
using WinPlay.Core.Rtsp;

namespace WinPlay.Core.Raop;

public sealed record ProbeResponse(string Phase, string Method, int StatusCode,
    Dictionary<string, object?> Fields, bool PlistParsed);

/// <summary>Negotiation evidence only. A 200 response never establishes audible or low-latency output.</summary>
public sealed class StreamProbeReport
{
    public int SchemaVersion => 1;
    public string Mode { get; init; } = "setup-probe";
    public StreamProbeOptions? Requested { get; init; }
    public string Outcome { get; internal set; } = "not-run";
    public string Phase { get; internal set; } = "not-started";
    public string? ErrorKind { get; internal set; }
    public ReceiverCapabilitySnapshot? Capabilities { get; internal set; }
    public List<ProbeResponse> Responses { get; } = [];
    public bool StreamSetupAccepted => Responses.Any(r => r.Phase == "stream-setup" && r.StatusCode == 200);
    public bool StreamSetupResponseParsed => Responses.Any(r => r.Phase == "stream-setup" && r.StatusCode == 200 && r.PlistParsed);
    public string AudibleOutput => "not-tested";
    public double? MeasuredOutputLatencyMs => null;
    public string Stability => "not-tested";
    public string StereoPairAndMultiRoom => "not-tested";
    public long AudioPacketsSent { get; internal set; }
    public string TeardownOutcome { get; internal set; } = "not-attempted";

    internal void Observe(string phase, string method, RtspResponse response)
    {
        var fields = new Dictionary<string, object?>();
        bool parsed = false;
        // Pairing payloads and arbitrary error bodies are excluded entirely.
        if ((phase is "info" or "session-setup" or "stream-setup") && response.Body.Length > 0)
        {
            try
            {
                var body = BinaryPlist.ReadDictionary(response.Body);
                fields = ReceiverCapabilitySnapshot.Filter(body);
                if (phase == "info") Capabilities = ReceiverCapabilitySnapshot.Capture(body);
                parsed = true;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException) { }
        }
        Responses.Add(new(phase, method, response.StatusCode, fields, parsed));
    }
}
