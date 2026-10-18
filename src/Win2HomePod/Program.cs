using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinPlay.Core.Audio;
using WinPlay.Core.Discovery;
using WinPlay.Core.Raop;

namespace Win2HomePod;

internal static class Program
{
    public static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static readonly string State = Path.Combine(Root, ".state");
    private static readonly object LogLock = new();
    public static void Log(string message)
    {
        Directory.CreateDirectory(State);
        lock (LogLock)
        {
            var path = Path.Combine(State, "app.log");
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > 10_000_000 || info.LastWriteTime.Date < DateTime.Today)
                    File.Move(path, Path.Combine(State, $"app-{info.LastWriteTime:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log"));
            }
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n");
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(State);
        if (args.Contains("--stop"))
        {
            try { using var stop = EventWaitHandle.OpenExisting("Local\\Win2HomePod.Stop"); stop.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        using var single = new Mutex(true, "Local\\Win2HomePod.App", out bool first);
        if (!first)
        {
            try { using var show = EventWaitHandle.OpenExisting("Local\\Win2HomePod.Show"); show.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            if (args.Contains("--connect"))
                try { using var start = EventWaitHandle.OpenExisting("Local\\Win2HomePod.Start"); start.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        using var stopSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Win2HomePod.Stop");
        try
        {
            AudioRouting.Restore();
            Log($"APP START version={Application.ProductVersion} pid={Environment.ProcessId} session={Guid.NewGuid():N}");
            ApplicationConfiguration.Initialize();
            if (args.Contains("--latency-ms"))
            {
                int index = Array.IndexOf(args, "--latency-ms");
                var settings = AppSettings.Load();
                settings.LatencyMs = int.Parse(args[index + 1]);
                if (!AppSettings.SupportedLatencies.Contains(settings.LatencyMs)) throw new ArgumentException("Unsupported latency value");
                settings.Save();
            }
            using var form = new MainForm(!args.Contains("--no-connect") && !args.Contains("--render"), stopSignal, args.Contains("--render"));
            if (args.Contains("--render"))
            {
                form.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(State, "ui.png"));
                form.Close();
                return;
            }
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            MessageBox.Show(ex.Message, "Win2HomePod", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { AudioRouting.Restore(); }
    }
}

// Same WASAPI capture/resampler as the pinned upstream engine, with metering.
internal sealed class MeteredSource : IAudioSource
{
    private readonly LoopbackAudioSource source;
    public MeteredSource(MMDevice? device = null) => source = new LoopbackAudioSource(device);
    public float Gain = 1;
    private float peak;
    public float Peak => Volatile.Read(ref peak);
    public double QueueMs => source.BufferedMilliseconds;
    public double QueueHighWaterMs => source.HighWaterMilliseconds;
    public long DroppedFrames => source.DroppedFrames;
    public long UnderrunFrames => source.UnderrunFrames;
    public double MaxCaptureGapMs => source.MaxCallbackGapMilliseconds;
    public Exception? CaptureError => source.CaptureError;
    public long NonzeroBlocks;
    public void Read(Span<short> samples)
    {
        source.Read(samples);
        float gain = Volatile.Read(ref Gain);
        if (gain != 1)
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(samples[i] * gain);
        int max = 0;
        foreach (short s in samples) max = Math.Max(max, Math.Abs((int)s));
        Volatile.Write(ref peak, max / 32768f);
        if (max > 10) Interlocked.Increment(ref NonzeroBlocks);
    }
    public void Dispose() => source.Dispose();
}

internal sealed class AudioSession : IAsyncDisposable
{
    private readonly List<RaopSession> sessions = [];
    private readonly List<string> receiverNames = [];
    private BroadcastAudioSource? broadcast;
    public MeteredSource? Meter { get; private set; }
    public Action<string>? Status;
    public Action<Exception>? Fault;
    public long Packets => sessions.Count == 0 ? 0 : sessions.Min(s => s.FramesSent);
    public int Count => sessions.Count;
    public long Retransmits => sessions.Sum(s => s.RetransmitRequests);
    public long SendFailures => sessions.Sum(s => s.AudioSendFailures);
    public string ReceiverHealth => string.Join(";", sessions.Select((s, i) => $"{receiverNames[i]}:packets={s.FramesSent},rtx={s.RetransmitRequests},send_errors={s.AudioSendFailures}"));
    public string LatencySummary => string.Join(Environment.NewLine, sessions.Select((s, i) =>
        $"{receiverNames[i]}: 要求 {s.LatencyDiagnostics.RequestedMilliseconds} ms / 受信機 {s.LatencyDiagnostics.ReceiverSummary}"))
        + Environment.NewLine + "総遅延: 未計測（外部での音響測定が必要）";

    public async Task Start(IReadOnlyList<AirPlayDevice> devices, double db, bool tone, int latencyMs, MMDevice? captureDevice, CancellationToken ct)
    {
        var addresses = devices.Select(d => d.Addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork)).ToArray();
        for (int i = 0; i < devices.Count; i++)
        {
            string name = devices[i].Name;
            var session = await RaopSession.ConnectAsync(addresses[i], devices[i].AirPlayPort ?? 7000,
                true, addresses.Where(a => !a.Equals(addresses[i])).ToArray(),
                message => { Program.Log($"[{name}] {message}"); Status?.Invoke($"{name}: {message}"); }, ct, latencyMs: latencyMs);
            sessions.Add(session);
            receiverNames.Add(name);
            session.Faulted += ex => Fault?.Invoke(ex);
        }
        ct.ThrowIfCancellationRequested();
        IAudioSource source = tone ? new ChannelTestAudioSource(0.3) : (Meter = new MeteredSource(captureDevice));
        if (captureDevice is not null && Meter is not null)
            Meter.Gain = captureDevice.AudioEndpointVolume.Mute ? 0 : captureDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
        broadcast = new BroadcastAudioSource(source);
        // Create all branches before any pump starts, so both begin with the same sample.
        var branches = sessions.Select(_ => broadcast.CreateBranch()).ToArray();
        await Task.WhenAll(sessions.Select((s, i) => s.StartStreamingAsync(branches[i], db)));
        ct.ThrowIfCancellationRequested();
        Program.Log($"STREAM READY receivers={Count} source={(tone ? "channel-test" : captureDevice?.FriendlyName ?? "WASAPI loopback")} volume={db}dB latency_ms={latencyMs} capture_buffer_target_ms={LoopbackAudioSource.CaptureBufferTargetMs}");
    }
    public async Task Volume(double db)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(sessions.Select(s => s.SetVolumeAsync(db, timeout.Token)));
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var session in sessions)
        {
            try { await session.DisposeAsync(); }
            catch (Exception ex) { Program.Log($"Cleanup: {ex.Message}"); }
        }
        sessions.Clear();
        receiverNames.Clear();
        broadcast?.Dispose();
        broadcast = null;
        AudioRouting.Restore();
    }
}

internal sealed class DeviceItem(AirPlayDevice device)
{
    public AirPlayDevice Device => device;
    public override string ToString() => $"{device.Name}    {device.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)}";
}
internal sealed record LatencyItem(int Milliseconds)
{
    public override string ToString() => $"{Milliseconds} ms{(Milliseconds < 250 ? "（実験設定）" : Milliseconds == 2000 ? "（従来設定）" : "")}";
}

internal sealed class MainForm : Form
{
    private readonly CheckedListBox targets = new() { CheckOnClick = true, Height = 90, Dock = DockStyle.Fill };
    private readonly Label state = new() { Text = "HomePod mini を探しています…", AutoSize = true, MaximumSize = new Size(630, 0) };
    private readonly Label sourceLabel = new() { AutoSize = true };
    private readonly Label stats = new() { AutoSize = true, Text = "停止中" };
    private readonly TextBox latencyDiagnostics = new()
    {
        ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Height = 72, Text = "受信機の待ち時間応答: 未接続 / 総遅延: 未計測",
    };
    private readonly Label volumeLabel = new() { AutoSize = true, Text = "HomePod 音量: -18 dB" };
    private readonly TrackBar volume = new() { Minimum = -30, Maximum = 0, Value = -18, TickFrequency = 5, Dock = DockStyle.Fill };
    private readonly Label localPlayback = new() { Text = "PC側も再生します（消音には仮想音声出力が必要）", AutoSize = true };
    private readonly CheckBox virtualOutput = new() { Text = "Windowsの出力をVB-CABLEへ切替（停止時に復元）", AutoSize = true };
    private readonly ComboBox latency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly AppSettings settings = AppSettings.Load();
    private readonly Button connect = new() { Text = "音声を送信", AutoSize = true };
    private readonly Button stop = new() { Text = "停止", Enabled = false, AutoSize = true };
    private readonly Button test = new() { Text = "左右テスト（12秒）", AutoSize = true };
    private readonly Button refresh = new() { Text = "再検出", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private AirPlayBrowser? browser;
    private DiscoveryTransport? discoveryTransport;
    private AudioSession? audio;
    private CancellationTokenSource? connecting;
    private MMDevice? cableDevice;
    private bool usingCable;
    private int volumeLogCountdown;
    private bool closing, allowClose, busy, faulted, stopping;
    private long lastLog;
    private string? testSession;
    private readonly bool autoConnect;
    private readonly EventWaitHandle stopSignal;
    private readonly EventWaitHandle showSignal = new(false, EventResetMode.AutoReset, "Local\\Win2HomePod.Show");
    private readonly EventWaitHandle startSignal = new(false, EventResetMode.AutoReset, "Local\\Win2HomePod.Start");
    private readonly TrayController tray;

    public MainForm(bool autoConnect, EventWaitHandle stopSignal, bool preview = false)
    {
        this.autoConnect = autoConnect;
        this.stopSignal = stopSignal;
        Text = "Win2HomePod — Windows → HomePod mini";
        Font = new Font("Yu Gothic UI", 10);
        ClientSize = new Size(710, 720);
        MinimumSize = new Size(700, 750);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22), ColumnCount = 1, RowCount = 13 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Windowsの音を、HomePodへ。", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), AutoSize = true });
        layout.Controls.Add(new Label { Text = "出力先（複数選択できます）", AutoSize = true, Margin = new Padding(0, 16, 0, 6) });
        layout.Controls.Add(targets);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.Controls.Add(sourceLabel);
        layout.Controls.Add(virtualOutput);
        layout.Controls.Add(localPlayback);
        latency.Items.AddRange(AppSettings.SupportedLatencies.Select(ms => (object)new LatencyItem(ms)).ToArray());
        latency.SelectedIndex = Array.IndexOf(AppSettings.SupportedLatencies, settings.LatencyMs);
        volume.Value = settings.VolumeDb;
        volumeLabel.Text = $"HomePod 音量: {volume.Value} dB";
        var latencyRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        latencyRow.Controls.AddRange([new Label { Text = "再生待ち時間", AutoSize = true }, latency]);
        layout.Controls.Add(latencyRow);
        layout.Controls.Add(volumeLabel);
        layout.Controls.Add(volume);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.AddRange([connect, stop, test, refresh]);
        layout.Controls.Add(buttons);
        var statusPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        statusPanel.Controls.AddRange([state, stats]);
        layout.Controls.Add(statusPanel);
        layout.Controls.Add(latencyDiagnostics);
        layout.Controls.Add(new Label { Text = "音が途切れる場合は1秒または2秒に戻して再送信してください。\n仮想出力の切替にはVB-CABLEを別途インストールしてください。\n閉じると送信を停止し、元の出力デバイスへ戻します。", AutoSize = true, ForeColor = Color.DimGray });
        Controls.Add(layout);
        tray = new TrayController(this, () => StartAudio(false), () => StopAudio());
        connect.Click += async (_, _) => await StartAudio(false);
        stop.Click += async (_, _) => await StopAudio();
        test.Click += async (_, _) => await StartAudio(true);
        refresh.Click += async (_, _) => await Discover();
        virtualOutput.CheckedChanged += (_, _) => localPlayback.Visible = !virtualOutput.Checked;
        volume.ValueChanged += (_, _) => volumeLabel.Text = $"HomePod 音量: {volume.Value} dB";
        volume.MouseUp += async (_, _) => await ApplyVolume();
        volume.KeyUp += async (_, _) => await ApplyVolume();
        timer.Tick += (_, _) => Tick();
        Shown += async (_, _) =>
        {
            if (preview) return;
            DetectLocal();
            await Discover();
            if (closing) return;
            timer.Start();
            if (this.autoConnect && settings.TargetDeviceIds.Length > 0 && targets.Items.Count > 0)
            {
                var found = targets.Items.Cast<DeviceItem>().Select(i => i.Device.DeviceId).ToHashSet();
                if (settings.TargetDeviceIds.Any(id => !found.Contains(id)))
                {
                    state.Text = "保存済みの出力先が見つかりません。再検出してください。";
                    Program.Log("AUTO CONNECT withheld: a saved receiver is missing");
                }
                else
                {
                    await StartAudio(false);
                    if (audio is not null) tray.Hide();
                }
            }
        };
        FormClosing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            connecting?.Cancel();
            while (busy || stopping) await Task.Delay(100);
            await StopAudio();
            timer.Stop();
            browser?.Dispose();
            discoveryTransport?.Dispose();
            cableDevice?.Dispose();
            tray.Dispose();
            showSignal.Dispose();
            startSignal.Dispose();
            allowClose = true;
            Close();
        };
    }

    private void DetectLocal()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var source = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        sourceLabel.Text = $"再生音: {source.FriendlyName}";
        Program.Log($"Capture endpoint: {source.FriendlyName} ({source.ID})");
        cableDevice?.Dispose();
        var cableId = AudioRouting.FindCable();
        cableDevice = cableId is null ? null : enumerator.GetDevice(cableId);
        virtualOutput.Enabled = cableDevice is not null;
        virtualOutput.Checked = cableDevice is not null && settings.VirtualOutput;
        localPlayback.Visible = !virtualOutput.Checked;
    }

    private async Task Discover()
    {
        busy = true;
        connect.Enabled = test.Enabled = refresh.Enabled = false;
        state.Text = "HomePod mini を探しています…";
        try
        {
            var selected = targets.Items.Count == 0 ? settings.TargetDeviceIds.ToHashSet() : targets.CheckedItems.Cast<DeviceItem>().Select(i => i.Device.DeviceId).ToHashSet();
            browser?.Dispose();
            discoveryTransport?.Dispose();
            discoveryTransport = new DiscoveryTransport();
            browser = new AirPlayBrowser(discoveryTransport);
            browser.Start();
            var discoveryStarted = Stopwatch.StartNew();
            while (discoveryStarted.Elapsed < TimeSpan.FromSeconds(8))
            {
                await Task.Delay(250);
                var ready = browser.Snapshot().Where(d => d.Subtype == AirPlayDeviceSubtype.HomePod && d.Addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork)).ToArray();
                // With no saved targets, use the full discovery window so the user
                // can choose from all receivers, regardless of their number.
                bool found = settings.TargetDeviceIds.Length > 0 && settings.TargetDeviceIds.All(id => ready.Any(d => d.DeviceId == id));
                if (found && discoveryStarted.ElapsedMilliseconds >= 1500) break;
            }
            var devices = browser.Snapshot().Where(d => d.Subtype == AirPlayDeviceSubtype.HomePod && d.Addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork)).OrderBy(d => d.Name).ToArray();
            targets.Items.Clear();
            foreach (var device in devices) targets.Items.Add(new DeviceItem(device), selected.Contains(device.DeviceId));
            Program.Log($"DISCOVERY count={devices.Length} elapsed_ms={discoveryStarted.ElapsedMilliseconds} devices={string.Join(", ", devices.Select(d => d.Name))}");
            state.Text = devices.Length == 0 ? "HomePodが見つかりません。同じLANに接続して再検出してください。" : $"{devices.Length}台を検出しました。送信を開始できます。";
        }
        catch (Exception ex) { state.Text = ex.Message; Program.Log(ex.ToString()); }
        finally { busy = false; connect.Enabled = test.Enabled = targets.Items.Count > 0; refresh.Enabled = true; }
    }

    private async Task StartAudio(bool tone)
    {
        if (busy || stopping || audio is not null) return;
        var devices = targets.CheckedItems.Cast<DeviceItem>().Select(i => i.Device).ToArray();
        if (devices.Length == 0) { state.Text = "出力先を選択してください。"; return; }
        busy = true;
        faulted = false;
        SetControls(true);
        connecting = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            settings.LatencyMs = ((LatencyItem)latency.SelectedItem!).Milliseconds;
            settings.VirtualOutput = virtualOutput.Checked;
            settings.TargetDeviceIds = devices.Select(d => d.DeviceId).ToArray();
            settings.VolumeDb = volume.Value;
            settings.Save();
            usingCable = !tone && virtualOutput.Checked && cableDevice is not null;
            audio = new AudioSession();
            audio.Status = text => { if (IsHandleCreated && !IsDisposed) BeginInvoke(() => { if (busy) state.Text = text; }); };
            audio.Fault = ex => { Program.Log($"SESSION FAULT: {ex}"); faulted = true; };
            await audio.Start(devices, volume.Value, tone, settings.LatencyMs, usingCable ? cableDevice : null, connecting.Token);
            if (usingCable)
            {
                AudioRouting.UseCable(cableDevice!.ID);
            }
            state.Text = $"送信中: {string.Join(" + ", devices.Select(d => d.Name))}{(tone ? "（左右テスト）" : "")}";
            latencyDiagnostics.Text = audio.LatencySummary;
            tray.Status($"HomePod: {string.Join(" + ", devices.Select(d => d.Name))} / {settings.LatencyMs}ms");
            lastLog = 0;
            if (tone)
            {
                var marker = testSession = Guid.NewGuid().ToString();
                _ = EndTest(marker);
            }
        }
        catch (Exception ex)
        {
            Program.Log(ex.ToString());
            await StopAudio(true);
            state.Text = ex is OperationCanceledException ? "接続をキャンセルしました。" : $"接続できません: {ex.Message}";
        }
        finally { connecting.Dispose(); connecting = null; busy = false; SetControls(audio is not null); }
    }

    private async Task EndTest(string marker)
    {
        await Task.Delay(12000);
        if (testSession == marker && !closing) await StopAudio();
    }
    private async Task ApplyVolume()
    {
        if (audio is null || busy) return;
        try { await audio.Volume(volume.Value); }
        catch (Exception ex) { Program.Log(ex.ToString()); state.Text = $"音量を変更できません: {ex.Message}"; }
        settings.VolumeDb = volume.Value;
        settings.Save();
    }
    private async Task StopAudio(bool force = false)
    {
        if (!force && busy && connecting is not null) { connecting.Cancel(); return; }
        if (stopping) return;
        stopping = true;
        testSession = null;
        var old = audio;
        audio = null;
        SetControls(true);
        state.Text = "停止しています…";
        if (old is not null) await old.DisposeAsync();
        AudioRouting.Restore();
        usingCable = false;
        SetControls(false);
        state.Text = "停止しました。Windows出力を復元しました。";
        tray.Status("Win2HomePod — 停止中");
        stats.Text = "停止中";
        latencyDiagnostics.Text = "受信機の待ち時間応答: 未接続 / 総遅延: 未計測";
        Program.Log("STOPPED");
        stopping = false;
    }
    private void SetControls(bool active)
    {
        connect.Enabled = test.Enabled = refresh.Enabled = targets.Enabled = !active;
        stop.Enabled = active;
        virtualOutput.Enabled = !active && cableDevice is not null;
        latency.Enabled = !active;
        volume.Enabled = !busy;
    }
    private async void Tick()
    {
        if (stopSignal.WaitOne(0)) { Close(); return; }
        if (showSignal.WaitOne(0)) tray.Show();
        if (startSignal.WaitOne(0) && !busy) await StartAudio(false);
        if (faulted && !busy) { faulted = false; await StopAudio(); state.Text = "接続が切れたため停止しました。再送信できます。"; return; }
        if (audio is null || busy) return;
        if (audio.Meter?.CaptureError is { } captureError)
        {
            Program.Log($"CAPTURE FAULT: {captureError}");
            faulted = true;
            return;
        }
        if (usingCable && audio.Meter is not null && cableDevice is not null)
        {
            try
            {
                float gain = cableDevice.AudioEndpointVolume.Mute ? 0 : cableDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                if (gain != audio.Meter.Gain) volumeLogCountdown = 3;
                audio.Meter.Gain = gain;
                if (volumeLogCountdown > 0 && --volumeLogCountdown == 0)
                    Program.Log($"WINDOWS VOLUME gain={gain:F4} output_peak={audio.Meter.Peak:F4}");
            }
            catch (Exception ex) { Program.Log($"Volume endpoint unavailable: {ex.Message}"); faulted = true; }
        }
        float peak = audio.Meter?.Peak ?? 0;
        stats.Text = $"{audio.Count}台へ送信  ·  {audio.Packets:N0} パケット/台  ·  入力レベル {(peak < .0001 ? "無音" : $"{20 * Math.Log10(peak):F0} dBFS")}";
        if (audio.Packets - lastLog > 1200)
        {
            lastLog = audio.Packets;
            Program.Log($"HEALTH receivers={audio.Count} packets_min={audio.Packets} nonzero_blocks={audio.Meter?.NonzeroBlocks ?? 0} peak={peak:F4} retransmits={audio.Retransmits} send_failures={audio.SendFailures} capture_queue_ms={audio.Meter?.QueueMs:F1} capture_highwater_ms={audio.Meter?.QueueHighWaterMs:F1} capture_dropped_frames={audio.Meter?.DroppedFrames} capture_underrun_frames={audio.Meter?.UnderrunFrames} capture_gap_max_ms={audio.Meter?.MaxCaptureGapMs:F1} members=[{audio.ReceiverHealth}]");
        }
    }
}
