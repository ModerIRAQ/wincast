using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinCast.Models;

internal interface IWinCastPlugin
{
    string Name { get; }
    string? Prefix { get; }           // e.g., ">" for shell, null for always-active
    string? IconGlyph { get; }
    int Priority { get; }
    bool CanHandle(string query);
    Task<List<WinCast.Services.SearchEngine.SearchResult>> QueryAsync(string query, List<AppItem> apps, CancellationToken ct);
}
