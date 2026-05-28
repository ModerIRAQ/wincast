using System.Reflection;
using System.Text.Json;
using WinCast.Services;

string tempDir = Path.Combine(Path.GetTempPath(), "WinCast.SettingsTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
Environment.SetEnvironmentVariable("WINCAST_SETTINGS_DIR", tempDir);

try
{
    if (SettingsService.Instance.AiProvider != "Custom")
    {
        throw new InvalidOperationException("AI provider should default to Custom.");
    }

    const string dummyApiKey = "test-openrouter-key-for-storage";

    SettingsService.Instance.OpenRouterApiKey = dummyApiKey;
    SettingsService.Instance.AiProvider = "Custom";

    SettingsService.Save();

    string settingsPath = Path.Combine(tempDir, "settings.json");
    string json = File.ReadAllText(settingsPath);

    if (json.Contains(dummyApiKey, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("settings.json contains the plaintext OpenRouter API key.");
    }

    using JsonDocument document = JsonDocument.Parse(json);
    if (document.RootElement.TryGetProperty("OpenRouterApiKey", out JsonElement keyElement)
        && !string.IsNullOrEmpty(keyElement.GetString()))
    {
        throw new InvalidOperationException("settings.json persisted OpenRouterApiKey instead of protected storage.");
    }

    string protectedKeyPath = Path.Combine(tempDir, "openrouter-api-key.dat");
    if (!File.Exists(protectedKeyPath))
    {
        throw new InvalidOperationException("Protected OpenRouter API key file was not created.");
    }

    byte[] protectedFileBytes = File.ReadAllBytes(protectedKeyPath);
    byte[] plaintextBytes = System.Text.Encoding.UTF8.GetBytes(dummyApiKey);
    if (protectedFileBytes.AsSpan().IndexOf(plaintextBytes) >= 0)
    {
        throw new InvalidOperationException("Protected key file contains the plaintext OpenRouter API key.");
    }

    typeof(SettingsService)
        .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
        ?.SetValue(null, null);

    if (SettingsService.Instance.OpenRouterApiKey != dummyApiKey)
    {
        throw new InvalidOperationException("Protected OpenRouter API key did not round-trip after reload.");
    }

    Console.WriteLine("Settings storage tests passed.");
}
finally
{
    Directory.Delete(tempDir, recursive: true);
}
