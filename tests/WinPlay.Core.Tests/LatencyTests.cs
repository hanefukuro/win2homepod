using System.Net;
using System.Text.Json;
using Win2HomePod;
using WinPlay.Core.Plist;
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

public sealed class LatencyTests
{
    [Theory]
    [InlineData(40, 1764)]
    [InlineData(50, 2205)]
    [InlineData(60, 2646)]
    [InlineData(70, 3087)]
    [InlineData(80, 3528)]
    [InlineData(100, 4410)]
    [InlineData(150, 6615)]
    [InlineData(2000, 88200)]
    public void RequestedLatencyUses44100HzFrames(int milliseconds, int frames) =>
        Assert.Equal(frames, AirPlayLatency.ToFrames(milliseconds));

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(39)]
    [InlineData(2001)]
    [InlineData(int.MaxValue)]
    public async Task InvalidLatencyFailsBeforeConnecting(int milliseconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RaopSession.ConnectAsync(IPAddress.Loopback, 1, false, latencyMs: milliseconds));
    }

    [Fact]
    public void EveryUiChoiceIsAcceptedByTheProtocolAndDefaultsStayUnchanged()
    {
        Assert.Equal(150, new AppSettings().LatencyMs);
        Assert.Equal(AppSettings.SupportedLatencies.Length, AppSettings.SupportedLatencies.Distinct().Count());
        foreach (int milliseconds in AppSettings.SupportedLatencies)
            Assert.True(AirPlayLatency.ToFrames(milliseconds) > 0);
        foreach (int milliseconds in new[] { 40, 50, 60, 70, 80 })
            Assert.Contains(milliseconds, AppSettings.SupportedLatencies);
    }

    [Fact]
    public void SetupDiagnosticsPreserveScopeNestedFieldsAndRawValues()
    {
        // Round-trip through the real plist reader, as in the RTSP handshake.
        var session = BinaryPlist.ReadDictionary(BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["latency"] = 3087L,
            ["eventPort"] = 7001L,
            ["name"] = "private receiver name",
        }));
        var stream = BinaryPlist.ReadDictionary(BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["audioLatency"] = 4410L,
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["latencyMin"] = 1764L,
                    ["LatencyMax"] = 3528L,
                    ["arrivalToRender"] = 0.07,
                    ["latencyInfo"] = new Dictionary<string, object?> { ["samples"] = 2205L },
                    ["dataPort"] = 7002L,
                    ["shk"] = new byte[] { 1, 2, 3 },
                },
                new Dictionary<string, object?> { ["latency"] = 8820L },
            },
        }));

        var diagnostics = ReceiverLatencyDiagnostics.FromSetup(40, session, stream);
        Assert.Equal(40, diagnostics.RequestedMilliseconds);
        Assert.Equal(1764, diagnostics.RequestedFrames);
        Assert.Null(diagnostics.MeasuredOutputLatencyMs);
        var fields = diagnostics.ReceiverFields.ToDictionary(f => f.Path, f => f.Value);
        Assert.Equal(7, fields.Count);
        Assert.Equal(3087L, fields["session.latency"]);
        Assert.Equal(4410L, fields["stream.audioLatency"]);
        Assert.Equal(1764L, fields["stream.streams[0].latencyMin"]);
        Assert.Equal(3528L, fields["stream.streams[0].LatencyMax"]);
        Assert.Equal(0.07, fields["stream.streams[0].arrivalToRender"]);
        Assert.Equal(2205L, fields["stream.streams[0].latencyInfo.samples"]);
        Assert.Equal(8820L, fields["stream.streams[1].latency"]);

        string json = JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("private receiver name", json);
        Assert.DoesNotContain("Port", json);
        Assert.DoesNotContain("shk", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("MeasuredOutputLatencyMs").ValueKind);
    }

    [Fact]
    public void MissingFieldsStayUnknownRatherThanEchoingTheRequestedValue()
    {
        var diagnostics = ReceiverLatencyDiagnostics.FromSetup(80, [], new() { ["dataPort"] = 7000L });
        Assert.Empty(diagnostics.ReceiverFields);
        Assert.Equal("応答なし", diagnostics.ReceiverSummary);
        Assert.Null(diagnostics.MeasuredOutputLatencyMs);
    }

    [Fact]
    public void MalformedLatencyValuesDoNotBreakHandshakeOrExposePayloads()
    {
        var diagnostics = ReceiverLatencyDiagnostics.FromSetup(150, new()
        {
            ["latency"] = "sensitive unexpected payload",
            ["latencyData"] = new byte[] { 1, 2, 3 },
            ["latencyOther"] = double.NaN,
            ["latencyUnknown"] = null,
        });
        Assert.Equal(4, diagnostics.ReceiverFields.Count);
        Assert.All(diagnostics.ReceiverFields, field => Assert.Null(field.Value));
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(diagnostics));
    }
}
