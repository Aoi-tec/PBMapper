#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PBMapper
{
    // ボーン名を「本体 / 左右 / 連番」に分解したもの
    public readonly struct NameKey
    {
        public readonly string key;
        public readonly char side;
        public readonly string digits;

        public NameKey(string key, char side, string digits)
        {
            this.key = key ?? "";
            this.side = side;
            this.digits = digits ?? "";
        }

        public bool IsEmpty => key.Length == 0 && digits.Length == 0;
    }

    public static class FuzzyMatcher
    {
        public const float MinScore = 0.6f;

        static readonly char[] Separators = { '_', '-', '.', ' ', '　', '(', ')', '[', ']', ':', '/', '|' };

        public static string NormalizeKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var k = Parse(s);
            var sb = new StringBuilder(k.key);
            if (k.side != '\0') sb.Append(char.ToLowerInvariant(k.side));
            sb.Append(k.digits.Replace(".", ""));
            return sb.ToString();
        }


        public static NameKey Parse(string name)
        {
            if (string.IsNullOrEmpty(name)) return new NameKey("", '\0', "");

            var tokens = Tokenize(name);
            var keySb = new StringBuilder();
            var digitList = new List<string>();
            char side = '\0';

            foreach (var raw in tokens)
            {
                if (raw.Length == 0) continue;
                if (char.IsDigit(raw[0]))
                {
                    var d = raw.TrimStart('0');
                    digitList.Add(d.Length == 0 ? "0" : d);
                    continue;
                }

                string tok = raw.ToLowerInvariant();

                if (tok.IndexOf('左') >= 0 || tok.Contains("ひだり"))
                {
                    if (side == '\0') side = 'L';
                    tok = tok.Replace("左", "").Replace("ひだり", "");
                }
                else if (tok.IndexOf('右') >= 0 || tok.Contains("みぎ"))
                {
                    if (side == '\0') side = 'R';
                    tok = tok.Replace("右", "").Replace("みぎ", "");
                }

                if (tok == "l" || tok == "left") { if (side == '\0') side = 'L'; continue; }
                if (tok == "r" || tok == "right") { if (side == '\0') side = 'R'; continue; }

                if (tok.Length > 5 && tok.StartsWith("left", StringComparison.Ordinal))
                { if (side == '\0') side = 'L'; tok = tok.Substring(4); }
                else if (tok.Length > 6 && tok.StartsWith("right", StringComparison.Ordinal))
                { if (side == '\0') side = 'R'; tok = tok.Substring(5); }

                keySb.Append(tok);
            }

            return new NameKey(keySb.ToString(), side, string.Join(".", digitList));
        }

        static List<string> Tokenize(string s)
        {
            var list = new List<string>();
            var cur = new StringBuilder();
            void Flush() { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); } }

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (Array.IndexOf(Separators, c) >= 0) { Flush(); continue; }

                if (cur.Length > 0)
                {
                    char prev = s[i - 1];
                    bool boundary =
                        (char.IsDigit(prev) != char.IsDigit(c)) ||
                        (char.IsLower(prev) && char.IsUpper(c)) ||
                        (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < s.Length && char.IsLower(s[i + 1]));
                    if (boundary) Flush();
                }
                cur.Append(c);
            }
            Flush();
            return list;
        }


        public static float MatchScore(string src, string dst) => MatchScore(Parse(src), Parse(dst));

        public static float MatchScore(in NameKey a, in NameKey b)
        {
            if (a.IsEmpty || b.IsEmpty) return 0f;

            if (a.side != '\0' && b.side != '\0' && a.side != b.side) return 0f;

            float score;
            if (a.key.Length == 0 || b.key.Length == 0)
            {
                if (a.key.Length != b.key.Length) return 0f;
                score = 2.0f;
            }
            else if (a.key == b.key) score = 2.0f;
            else if (Mathf.Min(a.key.Length, b.key.Length) >= 3 && (a.key.Contains(b.key) || b.key.Contains(a.key))) score = 1.5f;
            else score = Similarity(a.key, b.key);

            if ((a.side != '\0') != (b.side != '\0')) score *= 0.75f;

            if (a.digits != b.digits)
            {
                if (a.digits.Length > 0 && b.digits.Length > 0) score *= 0.25f;
                else score *= 0.75f;
            }
            return score;
        }

        public static float Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;
            int n = a.Length, m = b.Length;
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
