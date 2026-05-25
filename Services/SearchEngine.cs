using System.Globalization;
using System.Text.RegularExpressions;
using WinCast.Models;

namespace WinCast.Services;

internal static class SearchEngine
{
    internal record SearchResult(
        AppItem Item,
        int Score,
        bool IsCalculator = false,
        string CalcResult = "",
        bool IsShellCommand = false,
        string ShellCommandText = "",
        bool IsHelp = false,
        string HelpDetail = "",
        bool IsSystemCommand = false,
        string SystemAction = "",
        bool IsWebUrl = false,
        string WebUrl = "",
        bool IsWebSearch = false);

    // ═══════════════════════════════════════════════════
    //  System Commands
    // ═══════════════════════════════════════════════════

    private static readonly (string Name, string Description, string Action, string IconHint)[] SystemCommands =
    {
        ("Sleep",             "Put the computer to sleep",         "sleep",        "power"),
        ("Shutdown",          "Shut down the computer",            "shutdown",     "power"),
        ("Restart",           "Restart the computer",              "restart",      "power"),
        ("Lock",              "Lock the workstation",              "lock",         "lock"),
        ("Sign Out",          "Sign out of the current session",   "signout",      "signout"),
        ("Log Off",           "Sign out of the current session",   "signout",      "signout"),
        ("Empty Recycle Bin", "Permanently delete all recycled items", "emptybin", "delete"),
        ("Screen Off",        "Turn off the display",              "screenoff",    "display"),
    };

    // ═══════════════════════════════════════════════════
    //  URL Detection
    // ═══════════════════════════════════════════════════

    private static readonly Regex UrlSchemeRegex = new(
        @"^https?://\S+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlDomainRegex = new(
        @"^[\w.-]+\.(com|org|net|io|dev|app|co|me|ai|gov|edu|uk|ca|de|fr|jp|xyz|info|biz|tv|gg|to|ly|sh)(/\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocalhostRegex = new(
        @"^localhost(:\d+)?(/\S*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ═══════════════════════════════════════════════════
    //  Main Search
    // ═══════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════
    //  Plugins Registration
    // ═══════════════════════════════════════════════════

    private static readonly List<IWinCastPlugin> Plugins = new()
    {
        new HelpPlugin(),
        new ShellCommandPlugin(),
        new UrlDetectionPlugin(),
        new CalculatorPlugin(),
        new SystemCommandsPlugin(),
        new AppSearchPlugin()
    };

    internal static List<SearchResult> Search(List<AppItem> apps, string query)
    {
        var results = new List<SearchResult>();

        if (string.IsNullOrEmpty(query))
        {
            int limit = Math.Min(8, apps.Count);
            for (int i = 0; i < limit; i++)
                results.Add(new SearchResult(apps[i], Score: 1));
            return results;
        }

        // Query each plugin that can handle the query
        foreach (var plugin in Plugins)
        {
            if (plugin.CanHandle(query))
            {
                var pluginResults = plugin.QueryAsync(query, apps, CancellationToken.None).GetAwaiter().GetResult();
                results.AddRange(pluginResults);
            }
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Web search fallback — if fewer than 3 real app results
        int appMatches = 0;
        foreach (var r in results)
        {
            if (!r.IsCalculator && !r.IsWebUrl && !r.IsSystemCommand)
                appMatches++;
        }
        if (appMatches <= 2 && !string.IsNullOrWhiteSpace(query) && !query.StartsWith(">") && !query.StartsWith("?"))
        {
            results.Add(new SearchResult(
                Item: new AppItem { Name = $"Search Google for \"{query}\"", Path = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}" },
                Score: -1,
                IsWebSearch: true));
        }

        return results;
     }

     // ═══════════════════════════════════════════════════
     //  Internal Plugin Implementations
     // ═══════════════════════════════════════════════════

     private class HelpPlugin : IWinCastPlugin
     {
         public string Name => "Help";
         public string? Prefix => "?";
         public string? IconGlyph => "\uE897";
         public int Priority => 100;
         public bool CanHandle(string query) => query.StartsWith("?");
         public Task<List<SearchResult>> QueryAsync(string query, List<AppItem> apps, CancellationToken ct)
         {
             return Task.FromResult(SearchHelp(query));
         }
     }

     private class ShellCommandPlugin : IWinCastPlugin
     {
         public string Name => "Shell";
         public string? Prefix => ">";
         public string? IconGlyph => "\uE756";
         public int Priority => 90;
         public bool CanHandle(string query) => query.StartsWith(">");
         public Task<List<SearchResult>> QueryAsync(string query, List<AppItem> apps, CancellationToken ct)
         {
             var results = new List<SearchResult>();
             string cmdText = query.Substring(1).TrimStart();
             if (string.IsNullOrEmpty(cmdText))
             {
                 results.Add(new SearchResult(
                     Item: new AppItem { Name = "Enter a command...", Path = "" },
                     Score: 1000,
                     IsShellCommand: true,
                     ShellCommandText: ""));
             }
             else
             {
                 results.Add(new SearchResult(
                     Item: new AppItem { Name = cmdText, Path = "Run command: " + cmdText },
                     Score: 1000,
                     IsShellCommand: true,
                     ShellCommandText: cmdText));
             }
             return Task.FromResult(results);
         }
     }

     private class UrlDetectionPlugin : IWinCastPlugin
     {
         public string Name => "URL";
         public string? Prefix => null;
         public string? IconGlyph => "\uE774";
         public int Priority => 80;
         public bool CanHandle(string query) => TryDetectUrl(query.Trim(), out _);
         public Task<List<SearchResult>> QueryAsync(string query, List<AppItem> apps, CancellationToken ct)
         {
             var results = new List<SearchResult>();
             string trimmed = query.Trim();
             if (TryDetectUrl(trimmed, out string resolvedUrl))
             {
                 results.Add(new SearchResult(
                     Item: new AppItem { Name = $"Open {trimmed}", Path = resolvedUrl },
                     Score: 300,
                     IsWebUrl: true,
                     WebUrl: resolvedUrl));
             }
             return Task.FromResult(results);
         }
     }

     private class CalculatorPlugin : IWinCastPlugin
     {
         public string Name => "Calculator";
         public string? Prefix => null;
         public string? IconGlyph => "\uE1D0";
         public int Priority => 70;
         public bool CanHandle(string query) => TryEvaluateMath(query, out _);
         public Task<List<SearchResult>> QueryAsync(string query, List<AppItem> apps, CancellationToken ct)
         {
             var results = new List<SearchResult>();
             if (TryEvaluateMath(query, out double calcResult))
             {
                 string resStr = FormatNumber(calcResult);
                 results.Add(new SearchResult(
                     Item: new AppItem { Name = "Calculator Result", Path = query },
                     Score: 200,
                     IsCalculator: true,
                     CalcResult: resStr));
             }
             return Task.FromResult(results);
         }
     }

     private class SystemCommandsPlugin : IWinCastPlugin
     {
         public string Name => "System Commands";
         public string? Prefix => null;
         public string? IconGlyph => "\uE770";
         public int Priority => 60;
         public bool CanHandle(string query)
         {
             foreach (var cmd in SystemCommands)
             {
                 if (CalculateFuzzyScore(cmd.Name, query) > 0)
                     return true;
             }
             return false;
         }
         public Task<List<SearchResult>> QueryAsync(string query, List<AppItem> apps, CancellationToken ct)
         {
             var results = new List<SearchResult>();
             foreach (var cmd in SystemCommands)
             {
                 int score = CalculateFuzzyScore(cmd.Name, query);
                 if (score > 0)
                 {
                     results.Add(new SearchResult(
                         Item: new AppItem { Name = cmd.Name, Path = cmd.Description },
                         Score: score,
                         IsSystemCommand: true,
                         SystemAction: cmd.Action));
                 }
             }
             return Task.FromResult(results);
         }
     }

     private class AppSearchPlugin : IWinCastPlugin
     {
         public string Name => "Applications";
         public string? Prefix => null;
         public string? IconGlyph => null;
         public int Priority => 50;
         public bool CanHandle(string query) => true;
         public Task<List<SearchResult>> QueryAsync(string query, List<AppItem> apps, CancellationToken ct)
         {
             var results = new List<SearchResult>();
             foreach (var app in apps)
             {
                 int score = CalculateFuzzyScore(app.Name, query);
                 if (score > 0)
                     results.Add(new SearchResult(app, score));
             }
             return Task.FromResult(results);
         }
     }

    // ═══════════════════════════════════════════════════
    //  Fuzzy Scoring
    // ═══════════════════════════════════════════════════

    internal static int CalculateFuzzyScore(string target, string query)
    {
        if (string.IsNullOrEmpty(query)) return 0;

        string t = target.ToLowerInvariant();
        string q = query.ToLowerInvariant();

        if (t == q) return 100;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 95;

        int pos = t.IndexOf(q, StringComparison.Ordinal);
        if (pos >= 0)
            return (pos > 0 && t[pos - 1] == ' ') ? 85 : 65;

        // Subsequence matching
        int qIdx = 0;
        int firstMatch = -1, lastMatch = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == q[qIdx])
            {
                if (qIdx == 0) firstMatch = i;
                lastMatch = i;
                qIdx++;
                if (qIdx == q.Length) break;
            }
        }

        if (qIdx == q.Length)
        {
            int span = lastMatch - firstMatch;
            int penalty = Math.Min(span - q.Length, 40);
            return 50 - penalty;
        }

        return 0;
    }

    // ═══════════════════════════════════════════════════
    //  Help
    // ═══════════════════════════════════════════════════

    private static List<SearchResult> SearchHelp(string query)
    {
        string helpQuery = query[1..].Trim();
        var helpResults = new List<SearchResult>
        {
            new(new AppItem { Name = "Alt + Space", Path = "Toggle WinCast launcher" }, 1000, IsHelp: true, HelpDetail: "Press Alt + Space globally from anywhere in Windows to show or hide the WinCast launcher window."),
            new(new AppItem { Name = "Enter", Path = "Launch, copy, or execute selected result" }, 950, IsHelp: true, HelpDetail: "Press Enter on any selected search result to launch the application, copy the calculator result, copy help text, or execute the terminal command."),
            new(new AppItem { Name = "Ctrl + Shift + Enter", Path = "Run selected item as administrator" }, 900, IsHelp: true, HelpDetail: "Press Ctrl + Shift + Enter to run the selected desktop application or shell command with elevated administrative privileges."),
            new(new AppItem { Name = "Ctrl + Shift + O", Path = "Reveal selected app in File Explorer" }, 850, IsHelp: true, HelpDetail: "Press Ctrl + Shift + O to open File Explorer and highlight the selected application's executable file."),
            new(new AppItem { Name = "Ctrl + C", Path = "Copy path, result, command, or help text" }, 800, IsHelp: true, HelpDetail: "Press Ctrl + C to copy the selected item's path, AUMID, calculation output, shell command, or help description to the clipboard."),
            new(new AppItem { Name = "Escape", Path = "Dismiss launcher or close settings" }, 750, IsHelp: true, HelpDetail: "Press Escape to dismiss the WinCast window or close the Settings panel."),
            new(new AppItem { Name = "> command", Path = "Run a terminal command" }, 700, IsHelp: true, HelpDetail: "Prefix your search query with '>' to enter Shell Command Mode. For example, typing '> ipconfig /all' and pressing Enter executes that command directly in a command prompt window. Use Ctrl + Shift + Enter to elevate it."),
            new(new AppItem { Name = "Math expression", Path = "Calculate instantly from the search box" }, 650, IsHelp: true, HelpDetail: "Type any math expression, such as '(15 + 25) * 4', 'sqrt(144)', '2^10', or 'sin(pi/2)' directly into the search bar. Supports +, -, *, /, ^, %, sqrt, sin, cos, tan, log, log10, abs, ceil, floor, round, pi, and e. Press Enter to copy the result.")
        };

        if (string.IsNullOrWhiteSpace(helpQuery))
            return helpResults;

        return helpResults
            .Where(result =>
                ContainsIgnoreCase(result.Item.Name, helpQuery) ||
                ContainsIgnoreCase(result.Item.Path, helpQuery) ||
                ContainsIgnoreCase(result.HelpDetail, helpQuery))
            .ToList();
    }

    private static bool ContainsIgnoreCase(string value, string query) =>
        value.Contains(query, StringComparison.OrdinalIgnoreCase);

    // ═══════════════════════════════════════════════════
    //  URL Detection
    // ═══════════════════════════════════════════════════

    private static bool TryDetectUrl(string input, out string resolvedUrl)
    {
        resolvedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Contains(' ')) return false;

        if (UrlSchemeRegex.IsMatch(input))
        {
            resolvedUrl = input;
            return true;
        }

        if (LocalhostRegex.IsMatch(input))
        {
            resolvedUrl = "http://" + input;
            return true;
        }

        if (UrlDomainRegex.IsMatch(input))
        {
            resolvedUrl = "https://" + input;
            return true;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════
    //  Number Formatting
    // ═══════════════════════════════════════════════════

    private static string FormatNumber(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "∞";
        if (double.IsNegativeInfinity(value)) return "-∞";

        string resStr = value.ToString("G10");
        if (resStr.Contains('.'))
        {
            resStr = resStr.TrimEnd('0').TrimEnd('.');
        }
        return resStr;
    }

    // ═══════════════════════════════════════════════════
    //  Math Evaluator (Enhanced Recursive-Descent Parser)
    // ═══════════════════════════════════════════════════
    //
    //  Grammar:
    //    Expression → Term (('+' | '-') Term)*
    //    Term       → Exponent (('*' | '/' | '%') Exponent)*
    //    Exponent   → Unary ('^' Unary)*      (right-associative)
    //    Unary      → ('-' Unary) | Call
    //    Call       → IDENTIFIER '(' Expression ')' | Primary
    //    Primary    → NUMBER '%'? | IDENTIFIER | '(' Expression ')'

    // Known function names and constant names
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sqrt", "sin", "cos", "tan", "log", "log10", "abs", "ceil", "floor", "round",
        "asin", "acos", "atan"
    };

    private static readonly Dictionary<string, double> Constants = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"] = Math.PI,
        ["e"] = Math.E
    };

    private static bool TryEvaluateMath(string query, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(query)) return false;

        // Quick validation: must have at least one digit or known constant, and an operator or function call
        bool hasDigit = false, hasOperator = false, hasAlpha = false;
        foreach (char c in query)
        {
            if (char.IsDigit(c)) hasDigit = true;
            else if (c is '+' or '-' or '*' or '/' or '^' or '%') hasOperator = true;
            else if (char.IsLetter(c) || c == '_') hasAlpha = true;
            else if (c is not ('(' or ')' or '.' or ' ' or '\t')) return false;
        }

        // Need at least: (digit+operator), (digit+function/constant), or (constant+operator)
        if (!hasDigit && !hasAlpha) return false;
        if (!hasOperator && !hasAlpha && !hasDigit) return false;
        // Pure text with no digits and no parens is not math
        if (!hasDigit && !query.Contains('(') && !hasOperator) return false;
        // Must have some expression element
        if (hasDigit && !hasOperator && !hasAlpha && !query.Contains('(')) return false;

        try
        {
            int pos = 0;
            result = ParseExpression(query, ref pos);
            SkipSpaces(query, ref pos);
            return pos == query.Length && !double.IsNaN(result);
        }
        catch
        {
            return false;
        }
    }

    private static void SkipSpaces(string s, ref int pos)
    {
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t')) pos++;
    }

    private static double ParseExpression(string s, ref int pos)
    {
        double val = ParseTerm(s, ref pos);
        while (pos < s.Length)
        {
            SkipSpaces(s, ref pos);
            if (pos >= s.Length) break;
            if (s[pos] == '+') { pos++; val += ParseTerm(s, ref pos); }
            else if (s[pos] == '-') { pos++; val -= ParseTerm(s, ref pos); }
            else break;
        }
        return val;
    }

    private static double ParseTerm(string s, ref int pos)
    {
        double val = ParseExponent(s, ref pos);
        while (pos < s.Length)
        {
            SkipSpaces(s, ref pos);
            if (pos >= s.Length) break;
            if (s[pos] == '*') { pos++; val *= ParseExponent(s, ref pos); }
            else if (s[pos] == '/')
            {
                pos++;
                double divisor = ParseExponent(s, ref pos);
                if (divisor == 0) throw new DivideByZeroException();
                val /= divisor;
            }
            else if (s[pos] == '%')
            {
                // Disambiguate: '%' as modulo operator vs percentage suffix
                // If next char is a digit, paren, or letter → it's modulo
                int next = pos + 1;
                SkipSpaces(s, ref next);
                if (next < s.Length && (char.IsDigit(s[next]) || s[next] == '(' || char.IsLetter(s[next]) || s[next] == '-'))
                {
                    pos++;
                    double mod = ParseExponent(s, ref pos);
                    if (mod == 0) throw new DivideByZeroException();
                    val %= mod;
                }
                else break;
            }
            else break;
        }
        return val;
    }

    private static double ParseExponent(string s, ref int pos)
    {
        double val = ParseUnary(s, ref pos);
        SkipSpaces(s, ref pos);
        if (pos < s.Length && s[pos] == '^')
        {
            pos++;
            // Right-associative: recurse into ParseExponent
            double exp = ParseExponent(s, ref pos);
            val = Math.Pow(val, exp);
        }
        return val;
    }

    private static double ParseUnary(string s, ref int pos)
    {
        SkipSpaces(s, ref pos);
        if (pos < s.Length && s[pos] == '-')
        {
            pos++;
            return -ParseUnary(s, ref pos);
        }
        return ParseCall(s, ref pos);
    }

    private static double ParseCall(string s, ref int pos)
    {
        SkipSpaces(s, ref pos);

        // Check if current position starts an identifier (function or constant)
        if (pos < s.Length && (char.IsLetter(s[pos]) || s[pos] == '_'))
        {
            int start = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
            string identifier = s[start..pos];
            SkipSpaces(s, ref pos);

            // Function call: identifier followed by '('
            if (pos < s.Length && s[pos] == '(')
            {
                if (!Functions.Contains(identifier))
                    throw new FormatException($"Unknown function: {identifier}");

                pos++; // consume '('
                double arg = ParseExpression(s, ref pos);
                SkipSpaces(s, ref pos);
                if (pos < s.Length && s[pos] == ')') pos++;

                return EvaluateFunction(identifier, arg);
            }

            // Constant
            if (Constants.TryGetValue(identifier, out double constVal))
                return constVal;

            throw new FormatException($"Unknown identifier: {identifier}");
        }

        return ParsePrimary(s, ref pos);
    }

    private static double ParsePrimary(string s, ref int pos)
    {
        SkipSpaces(s, ref pos);

        // Parenthesized expression
        if (pos < s.Length && s[pos] == '(')
        {
            pos++; // consume '('
            double val = ParseExpression(s, ref pos);
            SkipSpaces(s, ref pos);
            if (pos < s.Length && s[pos] == ')') pos++;
            return val;
        }

        // Number literal
        int start = pos;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
        if (pos == start) throw new FormatException("Expected number");
        double num = double.Parse(s[start..pos], CultureInfo.InvariantCulture);

        // Percentage suffix: '15%' → 0.15 (only if '%' is NOT followed by a digit/paren = modulo)
        if (pos < s.Length && s[pos] == '%')
        {
            int next = pos + 1;
            SkipSpaces(s, ref next);
            bool isModulo = next < s.Length && (char.IsDigit(s[next]) || s[next] == '(' || char.IsLetter(s[next]) || s[next] == '-');
            if (!isModulo)
            {
                pos++; // consume '%'
                num /= 100.0;
            }
        }

        return num;
    }

    private static double EvaluateFunction(string name, double arg)
    {
        return name.ToLowerInvariant() switch
        {
            "sqrt" => Math.Sqrt(arg),
            "sin" => Math.Sin(arg),
            "cos" => Math.Cos(arg),
            "tan" => Math.Tan(arg),
            "asin" => Math.Asin(arg),
            "acos" => Math.Acos(arg),
            "atan" => Math.Atan(arg),
            "log" => Math.Log(arg),
            "log10" => Math.Log10(arg),
            "abs" => Math.Abs(arg),
            "ceil" => Math.Ceiling(arg),
            "floor" => Math.Floor(arg),
            "round" => Math.Round(arg),
            _ => throw new FormatException($"Unknown function: {name}")
        };
    }
}
