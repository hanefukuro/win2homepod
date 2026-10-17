using System.Text.Json;
namespace Win2HomePod;
internal sealed class AppSettings
{
    public static readonly int[] SupportedLatencies = [100, 150, 200, 250, 350, 500, 1000, 2000];
    public int LatencyMs { get; set; } = 150;
    public bool VirtualOutput { get; set; } = true;
    public string[] TargetDeviceIds { get; set; } = [];
    public int VolumeDb { get; set; } = -18;
    private static string FilePath => Path.Combine(Program.State, "settings.json");
    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            if (!SupportedLatencies.Contains(settings.LatencyMs)) settings.LatencyMs = 150;
            settings.VolumeDb = Math.Clamp(settings.VolumeDb, -30, 0);
            settings.TargetDeviceIds ??= [];
            return settings;
        }
        catch { return new(); }
    }
    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
}
