using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Win2HomePod.Probe;
using WinPlay.Core.Discovery;
using WinPlay.Core.Plist;
using WinPlay.Core.Raop;
using WinPlay.Core.Rtsp;
using Xunit;

namespace WinPlay.Core.Tests;

public sealed class ProbeTests
{
    [Fact]
    public void DeclarationUsesObservedNumericValuesAndExplicitFrameUnits()
    {
        var options = ProbeCommand.Parse(["plan", "--stream-type", "0x60", "--codec", "1", "--audio-format", "0x800",
            "--spf", "176", "--sample-rate", "48000", "--audio-mode", "example", "--latency-min", "1920", "--latency-max", "3840", "--timing", "ntp"]).Stream;
        var wire = options.BuildStream(7001, new byte[32], 123);
        var decoded = BinaryPlist.ReadDictionary(BinaryPlist.Write(wire));
        Assert.Equal(96L, decoded["type"]);
        Assert.Equal(1L, decoded["ct"]);
        Assert.Equal(0x800L, decoded["audioFormat"]);
        Assert.Equal(176L, decoded["spf"]);
        Assert.Equal(48000L, decoded["sr"]);
        Assert.Equal(1920L, decoded["latencyMin"]);
        Assert.Equal(3840L, decoded["latencyMax"]);
        Assert.Equal("example", decoded["audioMode"]);
        Assert.False(options.UsePtp);
        Assert.DoesNotContain("shk", options.ToDeclaration().Keys);
    }

    [Theory]
    [InlineData("--spf", "0")]
    [InlineData("--spf", "8193")]
    [InlineData("--codec", "opus")]
    [InlineData("--stream-type", "-1")]
    [InlineData("--audio-format", "0")]
    [InlineData("--sample-rate", "0")]
    [InlineData("--audio-mode", "bad\r\nmode")]
    [InlineData("--latency-min", "-1")]
    [InlineData("--latency-min", "7000")]
    [InlineData("--latency-max", "441001")]
    [InlineData("--timing", "other")]
    [InlineData("--unknown", "1")]
    public void InvalidPlansFailBeforeAnyNetworkOperation(string flag, string value) =>
        Assert.ThrowsAny<ArgumentException>(() => ProbeCommand.Parse(["plan", flag, value]));

    [Theory]
    [InlineData("plan --spf")]
    [InlineData("plan --spf 176 --spf 88")]
    [InlineData("probe --host invalid --output ignored.json")]
    [InlineData("probe --host 127.0.0.1")]
    [InlineData("capture --host 127.0.0.1 --port 0 --output ignored.json")]
    [InlineData("capture --host 127.0.0.1 --timeout-seconds 0 --output ignored.json")]
    public void IncompleteOrAmbiguousCommandsAreRejected(string args) =>
        Assert.ThrowsAny<ArgumentException>(() => ProbeCommand.Parse(args.Split(' ')));

    [Fact]
    public async Task OfflinePlanHasNoAcceptedOrMeasuredClaim()
    {
        using var output = new StringWriter();
        Assert.Equal(0, await ProbeCommand.RunAsync(["plan"], output, TextWriter.Null));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("offline-plan", json.RootElement.GetProperty("Mode").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("AudioPacketsSent").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("MeasuredOutputLatencyMs").ValueKind);
        Assert.False(json.RootElement.TryGetProperty("Outcome", out _));
    }

    [Fact]
    public void CapabilitySnapshotPreservesRawFeatureBitsAndRemovesIdentity()
    {
        var info = new Dictionary<string, object?>
        {
            ["model"] = "AudioAccessoryExample", ["sourceVersion"] = "1.2.3", ["features"] = unchecked((long)0x8000000080000200UL),
            ["name"] = "private-name", ["deviceID"] = "private-id", ["pk"] = new byte[] { 1, 2, 3 },
            ["txtAirPlay"] = Encoding.UTF8.GetBytes("private-txt"),
            ["audioFormats"] = new List<object?> { new Dictionary<string, object?>
                { ["type"] = 96L, ["audioInputFormats"] = 12345L, ["name"] = "nested-secret" } },
        };
        var snapshot = ReceiverCapabilitySnapshot.Capture(info, new AirPlayDevice
        {
            DeviceId = "private-id", Name = "private-name", RawFeatures = 0x8000000080000200UL,
            AirPlayTxt = new Dictionary<string, string> { ["features"] = "0x80000200,0x80000000", ["pk"] = "private-key", ["pi"] = "private-pi" },
        });
        Assert.Equal("0x8000000080000200", snapshot.InfoFeaturesHex);
        Assert.Equal(snapshot.InfoFeaturesHex, snapshot.MdnsFeaturesHex);
        string json = JsonSerializer.Serialize(snapshot);
        Assert.Contains("12345", json);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("nested-secret", json);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(400, false)]
    [InlineData(461, false)]
    public void SetupStatusIsRecordedWithoutInferringPlayback(int status, bool accepted)
    {
        var report = new StreamProbeReport { Requested = new() };
        report.Observe("stream-setup", "SETUP", new()
        {
            StatusCode = status,
            Body = BinaryPlist.Write(new Dictionary<string, object?>
            {
                ["streams"] = new List<object?> { new Dictionary<string, object?> { ["arrivalToRenderLatencyMs"] = 70L,
                    ["latencyMin"] = 3087L, ["dataPort"] = 7000L, ["shk"] = new byte[32] } },
            }),
        });
        Assert.Equal(accepted, report.StreamSetupAccepted);
        Assert.Equal("not-tested", report.AudibleOutput);
        Assert.Null(report.MeasuredOutputLatencyMs);
        Assert.Equal(0, report.AudioPacketsSent);
        string json = JsonSerializer.Serialize(report);
        Assert.Contains("arrivalToRenderLatencyMs", json);
        Assert.DoesNotContain("shk", json);
        Assert.DoesNotContain("dataPort", json);
    }

    [Fact]
    public void MalformedAcceptedPlistDoesNotBecomeSuccessfulParsing()
    {
        var report = new StreamProbeReport { Requested = new() };
        report.Observe("stream-setup", "SETUP", new() { StatusCode = 200, Body = [1, 2, 3] });
        Assert.True(report.StreamSetupAccepted);
        Assert.False(report.StreamSetupResponseParsed);
        Assert.Empty(Assert.Single(report.Responses).Fields);
    }

    [Fact]
    public async Task RejectedReceiverIsTornDownWithoutPairingOrAudio()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var methods = new List<string>();
        var receiver = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            methods.Add((await ReadHead(stream, timeout.Token)).Split(' ')[0]);
            await Respond(stream, 403, [], timeout.Token);
            methods.Add((await ReadHead(stream, timeout.Token)).Split(' ')[0]);
            await Respond(stream, 200, [], timeout.Token);
        }, timeout.Token);
        var report = await RaopSession.ProbeAsync(IPAddress.Loopback, port, new() { UsePtp = false }, timeout.Token);
        await receiver;
        Assert.Equal(new[] { "GET", "TEARDOWN" }, methods);
        Assert.Equal("rejected", report.Outcome);
        Assert.Equal("info", report.Phase);
        Assert.Equal("accepted", report.TeardownOutcome);
        Assert.False(report.StreamSetupAccepted);
        Assert.Equal(0, report.AudioPacketsSent);
    }

    [Fact]
    public async Task CapabilityCaptureReadsActualRtspPlistWithoutPairing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiver = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            Assert.StartsWith("GET /info RTSP/1.0", await ReadHead(stream, timeout.Token));
            await Respond(stream, 200, BinaryPlist.Write(new Dictionary<string, object?>
                { ["model"] = "test-model", ["features"] = 512L, ["pk"] = new byte[32] }), timeout.Token);
            var buffer = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(buffer, timeout.Token));
        }, timeout.Token);
        var report = await ProbeCommand.CaptureAsync(IPAddress.Loopback, port, timeout.Token);
        await receiver;
        Assert.Equal("capabilities-captured", report.Outcome);
        Assert.Null(report.Requested);
        Assert.Equal("test-model", report.Capabilities!.GetInfo["model"]);
        Assert.Single(report.Responses);
    }

    [Fact]
    public async Task CancelledProbeStillTearsDownAndRecordsTheFailurePhase()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiver = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(limit.Token);
            var stream = client.GetStream();
            Assert.StartsWith("GET", await ReadHead(stream, limit.Token));
            cancel.Cancel();
            Assert.StartsWith("TEARDOWN", await ReadHead(stream, limit.Token));
            await Respond(stream, 200, [], limit.Token);
        }, limit.Token);
        var report = await RaopSession.ProbeAsync(IPAddress.Loopback, port, new() { UsePtp = false }, cancel.Token);
        await receiver;
        Assert.Equal("cancelled-or-timeout", report.Outcome);
        Assert.Equal("info", report.Phase);
        Assert.Equal("accepted", report.TeardownOutcome);
    }

    [Fact]
    public void ComparisonIgnoresObjectKeyOrdering()
    {
        using var a = JsonDocument.Parse("{\"Capabilities\":{\"features\":512,\"model\":\"x\"}}");
        using var b = JsonDocument.Parse("{\"Capabilities\":{\"model\":\"x\",\"features\":512}}");
        using var comparison = JsonDocument.Parse(JsonSerializer.Serialize(ProbeCommand.Compare(a.RootElement, b.RootElement)));
        Assert.Empty(comparison.RootElement.GetProperty("Differences").EnumerateArray());
    }

    [Fact]
    public async Task ImportedInfoAndTxtProduceReusableRedactedReportAndNeverOverwrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "win2homepod-probe-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string info = Path.Combine(directory, "info.bplist"), txt = Path.Combine(directory, "txt.json"), output = Path.Combine(directory, "report.json");
            await File.WriteAllBytesAsync(info, BinaryPlist.Write(new Dictionary<string, object?>
                { ["model"] = "sample", ["name"] = "secret-name", ["audioFormats"] = new List<object?> { 262144L } }));
            await File.WriteAllTextAsync(txt, "{\"airplay\":{\"features\":\"0x200,0x200\",\"pk\":\"secret-key\"},\"raop\":{\"cn\":\"0,1,2\"}}");
            var args = new[] { "import-info", "--input", info, "--txt-json", txt, "--output", output };
            Assert.Equal(0, await ProbeCommand.RunAsync(args, TextWriter.Null, TextWriter.Null));
            string saved = await File.ReadAllTextAsync(output);
            Assert.Contains("0x200,0x200", saved);
            Assert.Contains("262144", saved);
            Assert.DoesNotContain("secret", saved);
            Assert.DoesNotContain(directory, saved);
            await Assert.ThrowsAsync<IOException>(() => ProbeCommand.RunAsync(args, TextWriter.Null, TextWriter.Null));
            Assert.Equal(saved, await File.ReadAllTextAsync(output));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<string> ReadHead(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < 16000)
        {
            if (await stream.ReadAsync(buffer, ct) == 0) throw new IOException("connection closed");
            bytes.Add(buffer[0]);
            if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("header too large");
    }

    private static async Task Respond(NetworkStream stream, int status, byte[] body, CancellationToken ct)
    {
        byte[] header = Encoding.ASCII.GetBytes($"RTSP/1.0 {status} Test\r\nContent-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
    }
}
