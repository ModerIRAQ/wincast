#include "pch.h"
#include "SearchEngine.h"
#include <iomanip>

namespace wincast::search
{
    // Skip whitespace in expressions
    static void SkipSpaces(const wchar_t*& str)
    {
        while (*str == L' ' || *str == L'\t') str++;
    }

    static double ParseExpression(const wchar_t*& str);

    static double ParseFactor(const wchar_t*& str)
    {
        SkipSpaces(str);
        if (*str == L'(')
        {
            str++; // consume '('
            double val = ParseExpression(str);
            SkipSpaces(str);
            if (*str == L')') str++; // consume ')'
            return val;
        }
        
        wchar_t* end = nullptr;
        double val = wcstod(str, &end);
        if (str == end)
        {
            throw std::runtime_error("Invalid number");
        }
        str = end;
        return val;
    }

    static double ParseTerm(const wchar_t*& str)
    {
        double val = ParseFactor(str);
        while (true)
        {
            SkipSpaces(str);
            if (*str == L'*')
            {
                str++;
                val *= ParseFactor(str);
            }
            else if (*str == L'/')
            {
                str++;
                double divisor = ParseFactor(str);
                if (divisor == 0) throw std::runtime_error("Division by zero");
                val /= divisor;
            }
            else
            {
                break;
            }
        }
        return val;
    }

    static double ParseExpression(const wchar_t*& str)
    {
        double val = ParseTerm(str);
        while (true)
        {
            SkipSpaces(str);
            if (*str == L'+')
            {
                str++;
                val += ParseTerm(str);
            }
            else if (*str == L'-')
            {
                str++;
                val -= ParseTerm(str);
            }
            else
            {
                break;
            }
        }
        return val;
    }

    std::vector<SearchResult> SearchEngine::Search(
        std::vector<scanner::AppItem> const& apps, 
        std::wstring const& query)
    {
        std::vector<SearchResult> results;
        if (query.empty())
        {
            // If query is empty, return first 8 apps as recommendations
            int limit = std::min(8, static_cast<int>(apps.size()));
            for (int i = 0; i < limit; ++i)
            {
                SearchResult r;
                r.Item = apps[i];
                r.Score = 1; // Base score
                results.push_back(r);
            }
            return results;
        }

        // 1. Try math evaluation
        double calcResult = 0;
        if (TryEvaluateMath(query, calcResult))
        {
            // Format result beautifully (remove trailing zeros for integers)
            std::wstringstream wss;
            wss << std::setprecision(10) << calcResult;
            std::wstring resStr = wss.str();

            SearchResult r;
            r.IsCalculator = true;
            r.Score = 200; // Force to top
            r.CalcResult = resStr;
            r.Item.Name = L"Calculator Result";
            r.Item.Path = query;
            results.push_back(r);
        }

        // 2. Perform fuzzy search
        for (auto const& app : apps)
        {
            int score = CalculateFuzzyScore(app.Name, query);
            if (score > 0)
            {
                SearchResult r;
                r.Item = app;
                r.Score = score;
                results.push_back(r);
            }
        }

        // Sort results by score (descending)
        std::sort(results.begin(), results.end(), [](SearchResult const& a, SearchResult const& b) {
            return a.Score > b.Score;
        });

        return results;
    }

    int SearchEngine::CalculateFuzzyScore(std::wstring const& target, std::wstring const& query)
    {
        if (query.empty()) return 0;
        
        std::wstring t = target;
        std::wstring q = query;
        std::transform(t.begin(), t.end(), t.begin(), ::towlower);
        std::transform(q.begin(), q.end(), q.begin(), ::towlower);
        
        if (t == q) return 100;
        
        // Prefix matching
        if (t.find(q) == 0) return 95;
        
        // Word boundary matching
        size_t pos = t.find(q);
        if (pos != std::wstring::npos)
        {
            if (pos > 0 && t[pos - 1] == L' ')
            {
                return 85;
            }
            return 65;
        }
        
        // Subsequence matching with distance penalty
        size_t qIdx = 0;
        size_t firstMatch = std::wstring::npos;
        size_t lastMatch = 0;
        for (size_t i = 0; i < t.size(); ++i)
        {
            if (t[i] == q[qIdx])
            {
                if (qIdx == 0) firstMatch = i;
                lastMatch = i;
                qIdx++;
                if (qIdx == q.size()) break;
            }
        }
        
        if (qIdx == q.size())
        {
            int span = static_cast<int>(lastMatch - firstMatch);
            int penalty = std::min(span - static_cast<int>(q.size()), 40);
            return 50 - penalty;
        }
        
        return 0;
    }

    bool SearchEngine::TryEvaluateMath(std::wstring const& query, double& result)
    {
        // Basic syntax verification: must contain at least one operator/digit
        // and only valid math expression characters.
        bool hasOperator = false;
        bool hasDigit = false;
        for (wchar_t c : query)
        {
            if (::iswdigit(c))
            {
                hasDigit = true;
            }
            else if (c == L'+' || c == L'-' || c == L'*' || c == L'/')
            {
                hasOperator = true;
            }
            else if (c != L'(' && c != L')' && c != L'.' && c != L' ' && c != L'\t')
            {
                // Disallowed character for calculator
                return false;
            }
        }

        if (!hasDigit || !hasOperator) return false;

        try
        {
            const wchar_t* p = query.c_str();
            result = ParseExpression(p);
            
            // Check that we consumed the entire string (ignoring trailing whitespace)
            SkipSpaces(p);
            return (*p == L'\0');
        }
        catch (...)
        {
            return false;
        }
    }
}
