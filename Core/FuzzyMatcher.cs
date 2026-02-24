#if UNITY_EDITOR
using System;
using UnityEngine;

namespace PBMapper
{
    public static class FuzzyMatcher
    {
        public static string NormalizeKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant();
            char[] remove = { '_', '-', '.', ' ', '(', ')', '[', ']' };
            foreach (var c in remove) s = s.Replace(c.ToString(), "");
            s = s.Replace("left", "l").Replace("right", "r");
            s = s.Replace("ひだり", "l").Replace("みぎ", "r");
            return s.Trim();
        }

        public static float MatchScore(string src, string dst)
        {
            string a = NormalizeKey(src); string b = NormalizeKey(dst);
            if (a == b) return 2.0f;
            if (a.Contains(b) || b.Contains(a)) return 1.5f;
            return Similarity(a, b);
        }

        public static float Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;
            int n = a.Length, m = b.Length; if (n == 0) return m == 0 ? 1f : 0f;
            int[] prev = new int[m + 1], curr = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = (a[i - 1] == b[j - 1]) ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev; prev = curr; curr = tmp;
            }
            int dist = prev[m]; int maxLen = Mathf.Max(n, m);
            return 1f - (float)dist / maxLen;
        }
    }
}
#endif
