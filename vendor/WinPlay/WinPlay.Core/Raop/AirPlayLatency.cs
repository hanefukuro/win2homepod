// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Raop;

/// <summary>Requested AirPlay playback buffer, not measured end-to-end latency.</summary>
public static class AirPlayLatency
{
    public const int SampleRate = 44100;
    public const int MinimumMilliseconds = 40;
    public const int MaximumMilliseconds = 2000;

    public static int ToFrames(int latencyMs)
    {
        if (latencyMs is < MinimumMilliseconds or > MaximumMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(latencyMs));
        return SampleRate * latencyMs / 1000;
    }
}
