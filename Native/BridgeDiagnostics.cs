using System.Text.Json;
namespace SafeBridge.Native;

internal static class BridgeDiagnostics
{
    public static void Record(string operation, Exception error)
    {
        var details = JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow, pid = Environment.ProcessId, operation,
            bridge_version = Contracts.NativeVersion, error = error.ToString(),
            config_path = BridgeConfig.ConfigPath,
            local_app_data_environment = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            local_app_data_special_folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        });
        var candidates = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "GIS-Agent"),
            Path.Combine(Path.GetTempPath(), "GIS-Agent")
        };
        foreach (var folder in candidates)
        {
            try
            {
                if (!Path.IsPathFullyQualified(folder)) continue;
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "bridge-lifecycle.jsonl"), details + "\n");
                return;
            }
            catch { /* Try the next diagnostic location, then leave the bridge disabled. */ }
        }
    }
}

