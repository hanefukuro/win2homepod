// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Raop;

/// <summary>Experimental SETUP declarations only. Does not configure an audio encoder.</summary>
public sealed record StreamProbeOptions
{
    public long StreamType { get; init; } = 0x60;
    public long Codec { get; init; } = 2;
    public long AudioFormat { get; init; } = 0x40000;
    public int SamplesPerFrame { get; init; } = 352;
    public int SampleRate { get; init; } = 44100;
    public string AudioMode { get; init; } = "default";
    // SETUP latencyMin/Max are declared sample-frame counts, never CLI milliseconds.
    public int LatencyMinFrames { get; init; } = 6615;
    public int LatencyMaxFrames { get; init; } = 6615;
    public bool UsePtp { get; init; } = true;

    public void Validate()
    {
        if (StreamType is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(StreamType));
        if (Codec is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(Codec));
        if (AudioFormat <= 0) throw new ArgumentOutOfRangeException(nameof(AudioFormat));
        if (SamplesPerFrame is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(SamplesPerFrame));
        if (SampleRate is < 8000 or > 192000) throw new ArgumentOutOfRangeException(nameof(SampleRate));
        if (string.IsNullOrWhiteSpace(AudioMode) || AudioMode.Length > 64 || AudioMode.Any(char.IsControl))
            throw new ArgumentException("audio mode must be 1..64 non-control characters", nameof(AudioMode));
        if (LatencyMinFrames < 0 || LatencyMaxFrames < LatencyMinFrames || LatencyMaxFrames > SampleRate * 10)
            throw new ArgumentOutOfRangeException(nameof(LatencyMaxFrames), "latency is in sample frames; require 0 <= min <= max <= 10 seconds");
    }

    public Dictionary<string, object?> ToDeclaration()
    {
        Validate();
        return new()
        {
            ["type"] = StreamType, ["ct"] = Codec, ["audioFormat"] = AudioFormat,
            ["spf"] = (long)SamplesPerFrame, ["sr"] = (long)SampleRate, ["audioMode"] = AudioMode,
            ["latencyMin"] = (long)LatencyMinFrames, ["latencyMax"] = (long)LatencyMaxFrames,
            ["isMedia"] = true, ["supportsDynamicStreamID"] = false,
        };
    }

    internal Dictionary<string, object?> BuildStream(int controlPort, byte[] audioKey, ulong streamConnectionId)
    {
        var result = ToDeclaration();
        result["controlPort"] = (long)controlPort;
        result["shk"] = audioKey;
        result["streamConnectionID"] = unchecked((long)streamConnectionId);
        return result;
    }
}
