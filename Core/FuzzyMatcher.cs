#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace PBMapper
{
    /// <summary>
    /// ボーン名を「本体キー / 左右 / 連番」に分解したもの。
    /// 例: "J_Bip_L_UpperArm" → key="jbipupperarm", side=L, digits=""
    ///     "Hair_Front.002"   → key="hairfront",    side=-, digits="2"
    /// </summary>
    public readonly struct NameKey
    {
        public readonly string key;
        public readonly char side;     // 'L' / 'R' / '\0'
        public readonly string digits; // 数字グループを '.' 区切りで連結（先頭ゼロ除去）

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
        /// <summary>これ未満のスコアは「該当なし」とみなす</summary>
        public const float MinScore = 0.6f;

        static readonly char[] Separators = { '_', '-', '.', ' ', '　', '(', ')', '[', ']', ':', '/', '|' };

        /// <summary>互換用：区切り文字を除去して小文字化したキー（左右・連番を含む）</summary>
        public static string NormalizeKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var k = Parse(s);
            var sb = new StringBuilder(k.key);
            if (k.side != '\0') sb.Append(char.ToLowerInvariant(k.side));
            sb.Append(k.digits.Replace(".", ""));
            return sb.ToString();
        }

        // ===== 名前の分解 =====

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

                // 日本語の左右
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

                // 単独トークンの左右 (Hand_L / Left Hand / J_Bip_L_Hand / UpperArmL)
                if (tok == "l" || tok == "left") { if (side == '\0') side = 'L'; continue; }
                if (tok == "r" || tok == "right") { if (side == '\0') side = 'R'; continue; }

                // 小文字連結 "lefthand" / "righthand" など
                if (tok.Length > 5 && tok.StartsWith("left", StringComparison.Ordinal))
                { if (side == '\0') side = 'L'; tok = tok.Substring(4); }
                else if (tok.Length > 6 && tok.StartsWith("right", StringComparison.Ordinal))
                { if (side == '\0') side = 'R'; tok = tok.Substring(5); }

                keySb.Append(tok);
            }

            return new NameKey(keySb.ToString(), side, string.Join(".", digitList));
        }

        /// <summary>区切り文字・キャメルケース・文字/数字の境界でトークン化</summary>
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
                        (char.IsDigit(prev) != char.IsDigit(c)) ||                               // Hair1 → Hair / 1
                        (char.IsLower(prev) && char.IsUpper(c)) ||                               // UpperArm → Upper / Arm
                        (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < s.Length && char.IsLower(s[i + 1])); // LLeg → L / Leg
                    if (boundary) Flush();
                }
                cur.Append(c);
            }
            Flush();
            return list;
        }

        // ===== スコア =====

        public static float MatchScore(string src, string dst) => MatchScore(Parse(src), Parse(dst));

        /// <summary>
        /// 2.0 = 本体キー完全一致 / 1.5 = 包含 / それ以下 = 編集距離類似度。
        /// 左右が逆なら 0、連番が違えば大きく減点（Hair_1 と Hair_2 を混同しない）。
        /// </summary>
        public static float MatchScore(in NameKey a, in NameKey b)
        {
            if (a.IsEmpty || b.IsEmpty) return 0f;

            // 左右が明示されていて逆 → 別物
            if (a.side != '\0' && b.side != '\0' && a.side != b.side) return 0f;

            float score;
            if (a.key.Length == 0 || b.key.Length == 0)
            {
                // 名前が数字だけ等。キーが両方空で連番一致のみ同一扱い
                if (a.key.Length != b.key.Length) return 0f;
                score = 2.0f;
            }
            else if (a.key == b.key) score = 2.0f;
            else if (Mathf.Min(a.key.Length, b.key.Length) >= 3 && (a.key.Contains(b.key) || b.key.Contains(a.key))) score = 1.5f;
            else score = Similarity(a.key, b.key);

            // 片方だけ左右指定あり
            if ((a.side != '\0') != (b.side != '\0')) score *= 0.75f;

            // 連番
            if (a.digits != b.digits)
            {
                if (a.digits.Length > 0 && b.digits.Length > 0) score *= 0.25f; // 1 と 2 は別ボーン
                else score *= 0.75f;                                           // Spine と Spine1 など
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
