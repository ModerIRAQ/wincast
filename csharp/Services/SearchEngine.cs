using System.Globalization;
using WinCast.Models;

namespace WinCast.Services;

internal static class SearchEngine
{
    internal record SearchResult(AppItem Item, int Score, bool IsCalculator = false, string CalcResult = "");

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
