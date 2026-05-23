using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WinCast.Services;

internal class AppSettings
{
    public bool ShowPreview { get; set; } = true;
    public string ThemeMode { get; set; } = "Dark";
    public string BackdropType { get; set; } = "Mica";
    public string SurfaceOpacity { get; set; } = "Balanced";
    public bool LaunchOnStartup { get; set; } = false;
}

internal static class SettingsService
{
    private static readonly string _dataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCast");
    private static readonly string _filePath = Path.Combine(_dataDir, "settings.json");

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
            if (!File.Exists(_filePath)) return new AppSettings();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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
            Directory.CreateDirectory(_dataDir);
            var json = JsonSerializer.Serialize(Instance, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);

            // Apply Startup registry settings
            ApplyStartupSetting(Instance.LaunchOnStartup);
        }
        catch { /* Non-critical */ }
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
