using System.Globalization;
using WinCast.Models;

namespace WinCast.Services;

internal static class SearchEngine
{
    internal record SearchResult(AppItem Item, int Score, bool IsCalculator = false, string CalcResult = "", bool IsShellCommand = false, string ShellCommandText = "", bool IsHelp = false, string HelpDetail = "");

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

        if (query.StartsWith("?"))
        {
            return SearchHelp(query);
        }

        // Try shell command mode first
        if (query.StartsWith(">"))
        {
            string cmdText = query.Substring(1).TrimStart();
            if (string.IsNullOrEmpty(cmdText))
            {
                results.Add(new SearchResult(
                    Item: new AppItem { Name = "Enter a command...", Path = "" },
                    Score: 1000,
                    IsShellCommand: true,
                    ShellCommandText: ""
                ));
            }
            else
            {
                results.Add(new SearchResult(
                    Item: new AppItem { Name = cmdText, Path = "Run command: " + cmdText },
                    Score: 1000,
                    IsShellCommand: true,
                    ShellCommandText: cmdText
                ));
            }
            return results;
        }

        // Try math evaluation first
        if (TryEvaluateMath(query, out double calcResult))
        {
            string resStr = calcResult.ToString("G10");
            // Trim trailing zeros after decimal point
            if (resStr.Contains('.'))
            {
                resStr = resStr.TrimEnd('0').TrimEnd('.');
            }
            results.Add(new SearchResult(
                Item: new AppItem { Name = "Calculator Result", Path = query },
                Score: 200,
                IsCalculator: true,
                CalcResult: resStr));
        }

        // Fuzzy search
        foreach (var app in apps)
        {
            int score = CalculateFuzzyScore(app.Name, query);
            if (score > 0)
                results.Add(new SearchResult(app, score));
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

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
            new(new AppItem { Name = "Math expression", Path = "Calculate instantly from the search box" }, 650, IsHelp: true, HelpDetail: "Type any basic math expression, such as '(15 + 25) * 4', directly into the search bar. The calculation will instantly show at the top of the results. Press Enter to copy it.")
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

    private static bool TryEvaluateMath(string query, out double result)
    {
        result = 0;
        bool hasOperator = false, hasDigit = false;
        foreach (char c in query)
        {
            if (char.IsDigit(c)) hasDigit = true;
            else if (c is '+' or '-' or '*' or '/') hasOperator = true;
            else if (c is not ('(' or ')' or '.' or ' ' or '\t')) return false;
        }
        if (!hasDigit || !hasOperator) return false;

        try
        {
            int pos = 0;
            result = ParseExpression(query, ref pos);
            SkipSpaces(query, ref pos);
            return pos == query.Length;
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
        double val = ParseFactor(s, ref pos);
        while (pos < s.Length)
        {
            SkipSpaces(s, ref pos);
            if (pos >= s.Length) break;
            if (s[pos] == '*') { pos++; val *= ParseFactor(s, ref pos); }
            else if (s[pos] == '/')
            {
                pos++;
                double divisor = ParseFactor(s, ref pos);
                if (divisor == 0) throw new DivideByZeroException();
                val /= divisor;
            }
            else break;
        }
        return val;
    }

    private static double ParseFactor(string s, ref int pos)
    {
        SkipSpaces(s, ref pos);
        if (pos < s.Length && s[pos] == '(')
        {
            pos++; // consume '('
            double val = ParseExpression(s, ref pos);
            SkipSpaces(s, ref pos);
            if (pos < s.Length && s[pos] == ')') pos++;
            return val;
        }
        int start = pos;
        while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '.')) pos++;
        if (pos == start) throw new FormatException("Invalid number");
        return double.Parse(s[start..pos], CultureInfo.InvariantCulture);
    }
}
