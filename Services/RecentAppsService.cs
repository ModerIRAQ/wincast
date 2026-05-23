using System.Text.Json;
using WinCast.Models;

namespace WinCast.Services;

internal static class RecentAppsService
{
    private static readonly string _dataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCast");
    private static readonly string _filePath = Path.Combine(_dataDir, "recent.json");
    private const int MaxRecents = 10;

    internal static List<RecentAppEntry> Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<RecentAppEntry>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    internal static void Save(List<RecentAppEntry> recents)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var json = JsonSerializer.Serialize(recents, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { /* Non-critical */ }
    }

    internal static List<RecentAppEntry> Push(List<RecentAppEntry> recents, AppItem item)
    {
        var key = item.IsUWP ? item.AUMID : item.Path;
        if (string.IsNullOrEmpty(key)) return recents;

        // Remove existing entry if present
        recents.RemoveAll(r => r.Key == key);

        // Add to front
        recents.Insert(0, new RecentAppEntry
        {
            Key = key,
            Name = item.Name,
            IsUWP = item.IsUWP,
            AUMID = item.AUMID,
            Path = item.Path
        });

        // Trim to max
        if (recents.Count > MaxRecents)
            recents.RemoveRange(MaxRecents, recents.Count - MaxRecents);

        Save(recents);
        return recents;
    }
}

internal class RecentAppEntry
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsUWP { get; set; }
    public string AUMID { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
