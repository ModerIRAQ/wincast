#pragma once
#include "pch.h"
#include "AppScanner.h"

namespace wincast::search
{
    struct SearchResult
    {
        scanner::AppItem Item;
        int Score = 0;
        bool IsCalculator = false;
        std::wstring CalcResult;
    };

    class SearchEngine
    {
    public:
        // Performs fuzzy search and calculator evaluation on the query
        static std::vector<SearchResult> Search(
            std::vector<scanner::AppItem> const& apps, 
            std::wstring const& query);

    private:
        // Calculates fuzzy match score (0 = no match, 100 = perfect match)
        static int CalculateFuzzyScore(std::wstring const& target, std::wstring const& query);

        // Tries to evaluate standard mathematical expressions (e.g. "2+3*4")
        static bool TryEvaluateMath(std::wstring const& query, double& result);
    };
}
