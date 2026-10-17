using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Win2HomePod;

internal static class AudioRouting
{
    private record SavedDefault(Role Role, string PreviousId, string RoutedId);
    private static string RecoveryPath => Path.Combine(Program.State, "output-recovery.json");
    public static string? FindCable()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
                if (device.FriendlyName.StartsWith("CABLE Input (", StringComparison.OrdinalIgnoreCase))
                    return device.ID;
        }
        return null;
    }
    public static void UseCable(string id)
    {
        if (File.Exists(RecoveryPath)) throw new InvalidOperationException("前回の出力復元が未完了です。");
        using var enumerator = new MMDeviceEnumerator();
        var restore = new List<SavedDefault>();
        foreach (var role in new[] { Role.Console, Role.Multimedia })
        {
            using var previous = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            if (previous.ID != id) restore.Add(new(role, previous.ID, id));
        }
        if (restore.Count == 0) return;
        File.WriteAllText(RecoveryPath, JsonSerializer.Serialize(restore));
        foreach (var entry in restore) SetDefault(id, entry.Role);
        Program.Log("Windows default output switched to virtual audio output");
    }
    public static void Restore()
    {
        if (!File.Exists(RecoveryPath)) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var entries = JsonSerializer.Deserialize<List<SavedDefault>>(File.ReadAllText(RecoveryPath))!;
            foreach (var saved in entries)
            {
                using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, saved.Role);
                // Respect a device choice the user made during the session.
                if (current.ID == saved.RoutedId) SetDefault(saved.PreviousId, saved.Role);
            }
            File.Delete(RecoveryPath);
            Program.Log("Windows default output restored");
        }
        catch (Exception ex) { Program.Log($"Default output restore pending: {ex.Message}"); }
    }
    public static void SetDefault(string id, Role role)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try { Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role)); }
        finally { Marshal.ReleaseComObject(policy); }
    }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod(string id, int defaultPeriod, IntPtr period, IntPtr minimum);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
        [PreserveSig] int GetShareMode(string id, IntPtr mode);
        [PreserveSig] int SetShareMode(string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id, [MarshalAs(UnmanagedType.Bool)] bool effectsStore, ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool effectsStore, ref PropertyKey key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
        [PreserveSig] int SetEndpointVisibility(string id, int visible);
    }
}
