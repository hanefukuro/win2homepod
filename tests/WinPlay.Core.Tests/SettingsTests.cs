using Win2HomePod;
using Xunit;

namespace Win2HomePod
{
    // AppSettings is linked from the app to test persistence without loading WinForms.
    internal static class Program
    {
        public static readonly string State = Path.Combine(Path.GetTempPath(), "Win2HomePod.Tests-" + Guid.NewGuid());
    }
}

namespace WinPlay.Core.Tests
{
    public sealed class SettingsTests : IDisposable
    {
        public SettingsTests() => Directory.CreateDirectory(Program.State);
        public void Dispose() => Directory.Delete(Program.State, true);

        [Theory]
        [InlineData(40)]
        [InlineData(50)]
        [InlineData(60)]
        [InlineData(70)]
        [InlineData(80)]
        [InlineData(150)]
        [InlineData(2000)]
        public void ExperimentalAndExistingSettingsSurviveSaveAndLoad(int milliseconds)
        {
            new AppSettings { LatencyMs = milliseconds }.Save();
            Assert.Equal(milliseconds, AppSettings.Load().LatencyMs);
        }

        [Theory]
        [InlineData("{\"LatencyMs\":39}")]
        [InlineData("{\"LatencyMs\":2001}")]
        [InlineData("not json")]
        public void InvalidSettingsFallBackTo150Milliseconds(string json)
        {
            File.WriteAllText(Path.Combine(Program.State, "settings.json"), json);
            Assert.Equal(150, AppSettings.Load().LatencyMs);
        }

        [Fact]
        public void MissingSettingsUseExistingDefault() => Assert.Equal(150, AppSettings.Load().LatencyMs);
    }
}
