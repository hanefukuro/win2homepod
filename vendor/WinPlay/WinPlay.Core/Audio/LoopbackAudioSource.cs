// SPDX-License-Identifier: GPL-3.0-or-later
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace WinPlay.Core.Audio;

/// <summary>
/// System-audio capture: WASAPI loopback → stereo downmix → WDL resample to 44.1 kHz →
/// S16 ring buffer. Note NAudio's documented gotcha: when nothing is playing,
/// DataAvailable never fires — <see cref="Read"/> zero-fills so the stream keeps flowing.
/// </summary>
public sealed class LoopbackAudioSource : IAudioSource
{
    private const int TargetRate = 44100;
    private readonly WasapiCapture _capture;
    private readonly MMDevice? _ownedDevice;
    private readonly WdlResampler _resampler;
    private readonly int _sourceChannels;
    private readonly int _sourceRate;

    private readonly object _lock = new();
    private readonly short[] _ring = new short[TargetRate * 2 * 4]; // 4 s stereo
    private int _ringRead, _ringWrite, _ringCount;
    private long _droppedSamples, _underrunSamples, _lastCallbackTick;
    private int _highWaterSamples;
    private double _maxCallbackGapMs;
    public Exception? CaptureError { get; private set; }
    public const int CaptureBufferTargetMs = 20;
    public double BufferedMilliseconds { get { lock (_lock) return _ringCount * 1000.0 / (TargetRate * 2); } }
    public double HighWaterMilliseconds { get { lock (_lock) return _highWaterSamples * 1000.0 / (TargetRate * 2); } }
    public long DroppedFrames => Interlocked.Read(ref _droppedSamples) / 2;
    // Underruns include genuinely idle/silent Windows output, not only playback faults.
    public long UnderrunFrames => Interlocked.Read(ref _underrunSamples) / 2;
    public double MaxCallbackGapMilliseconds { get { lock (_lock) return _maxCallbackGapMs; } }

    private float[] _floatScratch = new float[16384];
    private float[] _resampleOut = new float[16384];

    // Modified by Win2HomePod: capture a dedicated virtual render endpoint when provided.
    public LoopbackAudioSource(MMDevice? device = null)
    {
        if (device is null)
        {
            using var enumerator = new MMDeviceEnumerator();
            _ownedDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device = _ownedDevice;
        }
        _capture = new ShortLoopbackCapture(device);
        _sourceChannels = _capture.WaveFormat.Channels;
        _sourceRate = _capture.WaveFormat.SampleRate;

        _resampler = new WdlResampler();
        _resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(wantInputDriven: true);
        _resampler.SetRates(_sourceRate, TargetRate);

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => CaptureError = e.Exception;
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * _sourceChannels);
        if (frames == 0) return;

        EnsureCapacity(ref _floatScratch, frames * 2);

        // Convert to interleaved stereo float. The loopback mix format is float32
        // (WASAPI shared-mode Extensible resolves to IEEE float); tolerate PCM16 too.
        bool isFloat = _capture.WaveFormat.BitsPerSample == 32;
        for (int f = 0; f < frames; f++)
        {
            float l, r;
            if (isFloat)
            {
                int baseIdx = f * _sourceChannels * 4;
                l = BitConverter.ToSingle(e.Buffer, baseIdx);
                r = _sourceChannels > 1 ? BitConverter.ToSingle(e.Buffer, baseIdx + 4) : l;
            }
            else
            {
                int baseIdx = f * _sourceChannels * 2;
                l = BitConverter.ToInt16(e.Buffer, baseIdx) / 32768f;
                r = _sourceChannels > 1 ? BitConverter.ToInt16(e.Buffer, baseIdx + 2) / 32768f : l;
            }
            _floatScratch[f * 2] = l;
            _floatScratch[f * 2 + 1] = r;
        }

        // Resample to 44.1 kHz stereo.
        int needed = _resampler.ResamplePrepare(frames, 2, out float[] inBuf, out int inOff);
        Array.Copy(_floatScratch, 0, inBuf, inOff, Math.Min(needed, frames) * 2);
        int maxOut = (int)((long)frames * TargetRate / _sourceRate) + 16;
        EnsureCapacity(ref _resampleOut, maxOut * 2);
        int produced = _resampler.ResampleOut(_resampleOut, 0, frames, maxOut, 2);

        lock (_lock)
        {
            long tick = Stopwatch.GetTimestamp();
            if (_lastCallbackTick != 0)
                _maxCallbackGapMs = Math.Max(_maxCallbackGapMs, (tick - _lastCallbackTick) * 1000.0 / Stopwatch.Frequency);
            _lastCallbackTick = tick;
            for (int i = 0; i < produced * 2; i++)
            {
                float v = Math.Clamp(_resampleOut[i], -1f, 1f);
                _ring[_ringWrite] = (short)(v * 32767f);
                _ringWrite = (_ringWrite + 1) % _ring.Length;
                if (_ringCount == _ring.Length)
                    _ringRead = (_ringRead + 1) % _ring.Length; // overwrite oldest
                else
                    _ringCount++;
            }
            _highWaterSamples = Math.Max(_highWaterSamples, _ringCount);
            // A transient scheduling pause must not leave seconds of stale audio
            // queued forever. Healthy 20 ms callbacks stay well below this limit.
            const int maxQueuedSamples = TargetRate * 2 * 80 / 1000;
            if (_ringCount > maxQueuedSamples)
            {
                int drop = (_ringCount - TargetRate * 2 * 40 / 1000) & ~1;
                _ringRead = (_ringRead + drop) % _ring.Length;
                _ringCount -= drop;
                Interlocked.Add(ref _droppedSamples, drop);
            }
        }
    }

    public void Read(Span<short> interleavedStereo)
    {
        lock (_lock)
        {
            int available = Math.Min(_ringCount, interleavedStereo.Length);
            for (int i = 0; i < available; i++)
            {
                interleavedStereo[i] = _ring[_ringRead];
                _ringRead = (_ringRead + 1) % _ring.Length;
            }
            _ringCount -= available;
            Interlocked.Add(ref _underrunSamples, interleavedStereo.Length - available);
            interleavedStereo[available..].Clear();
        }
    }

    private static void EnsureCapacity(ref float[] buf, int needed)
    {
        if (buf.Length < needed)
            buf = new float[Math.Max(needed, buf.Length * 2)];
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch (InvalidOperationException) { }
        _capture.Dispose();
        _ownedDevice?.Dispose();
    }

    private sealed class ShortLoopbackCapture(MMDevice device) : WasapiCapture(device, true, CaptureBufferTargetMs)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
    }
}
