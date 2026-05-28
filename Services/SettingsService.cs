using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace WinCast.Services;

internal class AppSettings
{
    public bool ShowPreview { get; set; } = true;
    public string ThemeMode { get; set; } = "Dark";
    public string BackdropType { get; set; } = "Mica";
    public string SurfaceOpacity { get; set; } = "Balanced";
    public bool LaunchOnStartup { get; set; } = false;
    public string Language { get; set; } = "en";
    [JsonIgnore]
    public string OpenRouterApiKey { get; set; } = "";
    public string AiProvider { get; set; } = "Custom";
    public string FreeModel { get; set; } = "openrouter/free";
}

internal static class SettingsService
{
    private const string SettingsDirectoryEnvironmentVariable = "WINCAST_SETTINGS_DIR";
    private static readonly byte[] _apiKeyEntropy = Encoding.UTF8.GetBytes("WinCast.OpenRouterApiKey.v1");
    private static readonly string _dataDir =
        Environment.GetEnvironmentVariable(SettingsDirectoryEnvironmentVariable)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCast");
    private static readonly string _filePath = Path.Combine(_dataDir, "settings.json");
    private static readonly string _apiKeyPath = Path.Combine(_dataDir, "openrouter-api-key.dat");

    private static AppSettings? _instance;

    public static AppSettings Instance
    {
        get
        {
            if (_instance == null)
                _instance = Load();
            return _instance;
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                var defaultSettings = new AppSettings();
                try
                {
                    string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                    if (sysLang.Equals("ar", StringComparison.OrdinalIgnoreCase))
                    {
                        defaultSettings.Language = "ar";
                    }
                }
                catch { }
                return defaultSettings;
            }
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            var legacyApiKey = ReadLegacyApiKey(json);
            settings.OpenRouterApiKey = LoadProtectedOpenRouterApiKey();

            if (!string.IsNullOrWhiteSpace(legacyApiKey))
            {
                if (string.IsNullOrWhiteSpace(settings.OpenRouterApiKey))
                {
                    settings.OpenRouterApiKey = legacyApiKey;
                    SaveProtectedOpenRouterApiKey(legacyApiKey);
                }

                SaveSettingsFile(settings);
            }

            if (settings.AiProvider == "Free")
            {
                settings.AiProvider = "Custom";
                SaveSettingsFile(settings);
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save()
    {
        try
        {
            SaveProtectedOpenRouterApiKey(Instance.OpenRouterApiKey);
            SaveSettingsFile(Instance);

            // Apply Startup registry settings
            ApplyStartupSetting(Instance.LaunchOnStartup);
        }
        catch { /* Non-critical */ }
    }

    private static void SaveSettingsFile(AppSettings settings)
    {
        Directory.CreateDirectory(_dataDir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    private static string LoadProtectedOpenRouterApiKey()
    {
        try
        {
            if (!File.Exists(_apiKeyPath)) return string.Empty;

            byte[] protectedBytes = File.ReadAllBytes(_apiKeyPath);
            byte[] bytes = ProtectedData.Unprotect(protectedBytes, _apiKeyEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SaveProtectedOpenRouterApiKey(string? apiKey)
    {
        Directory.CreateDirectory(_dataDir);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            File.Delete(_apiKeyPath);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(apiKey.Trim());
        byte[] protectedBytes = ProtectedData.Protect(bytes, _apiKeyEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_apiKeyPath, protectedBytes);
    }

    private static string ReadLegacyApiKey(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("OpenRouterApiKey", out var value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ApplyStartupSetting(bool enabled)
    {
        try
        {
            string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
            if (key != null)
            {
                string exePath = Environment.ProcessPath ?? string.Empty;
                if (string.IsNullOrEmpty(exePath)) return;

                if (enabled)
                    key.SetValue("WinCast", $"\"{exePath}\" --background");
                else
                    key.DeleteValue("WinCast", false);
            }
        }
        catch { /* Registry access might be blocked or restricted */ }
    }
}
