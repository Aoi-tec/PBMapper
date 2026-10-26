#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

using VRC.SDK3.Dynamics.PhysBone.Components;

using Object = UnityEngine.Object;

namespace PBMapper
{
    /// <summary>参照リマップに必要な対応表と、結果レポート</summary>
    public sealed class RemapContext
    {
        public Dictionary<Component, Component> componentMap = new Dictionary<Component, Component>();
        public Dictionary<Transform, Transform> transformMap = new Dictionary<Transform, Transform>();
        public Dictionary<Transform, float> scores = new Dictionary<Transform, float>();
        public Transform sourceRoot;

        /// <summary>ソース側を指していたが対応先が見つからず None にした参照</summary>
        public readonly List<string> unresolved = new List<string>();
        /// <summary>名前の一致度が低いまま繋ぎ替えた参照（要確認）</summary>
        public readonly List<string> lowConfidence = new List<string>();
        readonly HashSet<Transform> reportedLow = new HashSet<Transform>();

        internal void NoteMapped(Transform src, Transform dst)
        {
            if (!src || !dst || reportedLow.Contains(src)) return;
            if (scores.TryGetValue(src, out var s) && s < 1.5f)
            {
                reportedLow.Add(src);
                lowConfidence.Add($"{src.GetHierarchyPath()} → {dst.GetHierarchyPath()} (score {s:F2})");
            }
        }
    }

    public static class PhysBoneMapperEngine
    {
        const float Threshold = FuzzyMatcher.MinScore;

        // ===== スキャン =====

        public static void Scan(
            List<MappingRow> rows,
            GameObject sourcePrefabRoot,
            GameObject targetPrefabRoot,
            ref Transform sourceArmatureRoot,
            ref Transform targetArmatureRoot,
            ref List<Transform> sourceAll,
            ref List<Transform> targetAll,
            bool scopePrefabWide,
            bool preferMABoneProxyRootForExternal)
        {
            rows.Clear();
            if (!sourcePrefabRoot || !targetPrefabRoot) return;

            sourceArmatureRoot = TransformUtilities.AutoDetectArmature(sourcePrefabRoot.transform);
            targetArmatureRoot = TransformUtilities.AutoDetectArmature(targetPrefabRoot.transform);

            sourceAll = TransformUtilities.Collect(scopePrefabWide ? sourcePrefabRoot.transform : sourceArmatureRoot);
            targetAll = TransformUtilities.Collect(scopePrefabWide ? targetPrefabRoot.transform : targetArmatureRoot);
            if (sourceAll.Count == 0 || targetAll.Count == 0) return;

            // 階層を考慮したボーン対応表（サジェストと Paste 時のリマップで同じロジックを使う）
            var scores = new Dictionary<Transform, float>();
            var map = BuildTransformMap(sourceAll, targetAll,
                RootSeeds(sourcePrefabRoot.transform, targetPrefabRoot.transform, sourceArmatureRoot, targetArmatureRoot),
                scores);

            var srcArm = sourceArmatureRoot;
            var dstArm = targetArmatureRoot;
            var dstAllLocal = targetAll;

            void AddRow(MappingRow.Kind kind, Component comp, Transform holder, Transform compRoot)
            {
                bool hasLocalMA = PBMapperHooks.HasLocalMABoneProxy?.Invoke(holder) ?? false;
                bool isOutside = srcArm && !TransformUtilities.IsUnderRoot(srcArm, holder);
                Transform proxyRoot = null;
                if (preferMABoneProxyRootForExternal && isOutside)
                    proxyRoot = PBMapperHooks.TryGetMABoneProxyRoot?.Invoke(holder);

                Transform cand; float score;
                if (proxyRoot)
                    (cand, score) = SuggestForBone(proxyRoot, map, scores, dstArm, dstAllLocal);
                else
                    (cand, score) = SuggestForBone(compRoot ? compRoot : holder, map, scores, null, dstAllLocal);

                string label = kind == MappingRow.Kind.PhysBone ? "PhysBone" : "Collider";
                string info = cand ? $"{score:F2}" : $"未検出 ({Mathf.Max(score, 0f):F2})";
                if (proxyRoot) info += $" (via:{proxyRoot.name})";

                rows.Add(new MappingRow
                {
                    kind = kind,
                    kindLabel = hasLocalMA ? "MA/" + label : label,
                    sourceComponent = comp,
                    sourceTransform = holder,
                    suggestedTarget = cand,
                    isMAExternal = proxyRoot,
                    maRootHint = proxyRoot,
                    maRootTarget = proxyRoot ? cand : null,
                    score = score,
                    info = info
                });
            }

            foreach (var t in sourceAll)
            {
                if (!t) continue;
                foreach (var pb in t.GetComponents<VRCPhysBone>())
                    AddRow(MappingRow.Kind.PhysBone, pb, t, pb.rootTransform);
                foreach (var col in t.GetComponents<VRCPhysBoneCollider>())
                    AddRow(MappingRow.Kind.Collider, col, t, col.rootTransform);
            }
        }

        // ===== サジェスト =====

        /// <summary>
        /// ソース側ボーンの対応先を返す。対応表にあればそれを、なければ名前のみで探索。
        /// restrictRoot を指定した場合はその配下の候補のみ。
        /// </summary>
        public static (Transform, float) SuggestForBone(
            Transform srcBone,
            Dictionary<Transform, Transform> map,
            Dictionary<Transform, float> scores,
            Transform restrictRoot,
            List<Transform> targetAll)
        {
            if (!srcBone) return (null, 0);
            if (map != null && map.TryGetValue(srcBone, out var dst) && dst &&
                (!restrictRoot || TransformUtilities.IsUnderRoot(restrictRoot, dst)))
            {
                float s = (scores != null && scores.TryGetValue(srcBone, out var v)) ? v : 2f;
                return (dst, s);
            }
            if (restrictRoot) return SuggestTargetByNameInSubtree(restrictRoot, srcBone.name);
            return SuggestTargetByName(srcBone.name, targetAll);
        }

        public static (Transform, float) SuggestTargetByName(string srcName, List<Transform> targetAll)
        {
            if (string.IsNullOrEmpty(srcName) || targetAll == null) return (null, 0);
            var sk = FuzzyMatcher.Parse(srcName);
            float bestScore = float.NegativeInfinity; Transform best = null;
            foreach (var t in targetAll)
            {
                if (!t) continue;
                float score = FuzzyMatcher.MatchScore(sk, FuzzyMatcher.Parse(t.name));
                if (score > bestScore) { bestScore = score; best = t; }
            }
            return (bestScore >= Threshold ? best : null, bestScore);
        }

        public static (Transform, float) SuggestTargetByNameInSubtree(Transform searchRoot, string srcName)
        {
            if (!searchRoot || string.IsNullOrEmpty(srcName)) return (null, 0);
            return SuggestTargetByName(srcName, TransformUtilities.Collect(searchRoot));
        }

        // ===== ボーン対応表 =====

        public static IEnumerable<KeyValuePair<Transform, Transform>> RootSeeds(
            Transform sourcePrefabRoot, Transform targetPrefabRoot,
            Transform sourceArmatureRoot, Transform targetArmatureRoot)
        {
            if (sourcePrefabRoot && targetPrefabRoot)
                yield return new KeyValuePair<Transform, Transform>(sourcePrefabRoot, targetPrefabRoot);
            if (sourceArmatureRoot && targetArmatureRoot)
                yield return new KeyValuePair<Transform, Transform>(sourceArmatureRoot, targetArmatureRoot);
        }

        /// <summary>
        /// ソース Transform → ターゲット Transform の対応表を作る。
        /// ・seeds（Prefab Root / Armature / UIで指定したターゲット）は最優先で確定
        /// ・親から順に処理し、「対応済みの親の直下／配下」にある候補へボーナスを与える
        ///   → 同名ボーン (HairFront/Hair_1 と HairBack/Hair_1 など) を取り違えない
        /// ・FuzzyMatcher で左右逆・連番違いは弾かれる
        /// </summary>
        public static Dictionary<Transform, Transform> BuildTransformMap(
            List<Transform> src, List<Transform> dst,
            IEnumerable<KeyValuePair<Transform, Transform>> seeds = null,
            Dictionary<Transform, float> scores = null)
        {
            var map = new Dictionary<Transform, Transform>();
            if (seeds != null)
            {
                foreach (var kv in seeds)
                {
                    if (!kv.Key || !kv.Value || map.ContainsKey(kv.Key)) continue;
                    map[kv.Key] = kv.Value;
                    if (scores != null) scores[kv.Key] = 2f;
                }
            }
            if (src == null || dst == null) return map;

            var dstKeys = new List<KeyValuePair<Transform, NameKey>>(dst.Count);
            foreach (var t in dst)
                if (t) dstKeys.Add(new KeyValuePair<Transform, NameKey>(t, FuzzyMatcher.Parse(t.name)));

            // Collect() は GetComponentsInChildren 順（親→子）なので、親の対応が先に決まる
            foreach (var s in src)
            {
                if (!s || map.ContainsKey(s)) continue;
                var sk = FuzzyMatcher.Parse(s.name);
                if (sk.IsEmpty) continue;

                Transform mappedAnc = null; bool ancIsParent = false;
                for (var p = s.parent; p; p = p.parent)
                {
                    if (map.TryGetValue(p, out var mp)) { mappedAnc = mp; ancIsParent = p == s.parent; break; }
                }

                Transform best = null;
                float bestRank = float.NegativeInfinity, bestRaw = 0f;
                int bestLevel = -1;
                foreach (var kv in dstKeys)
                {
                    float raw = FuzzyMatcher.MatchScore(sk, kv.Value);
                    if (raw < Threshold) continue;

                    var t = kv.Key;
                    int level = 0;
                    if (mappedAnc)
                    {
                        if (t.parent == mappedAnc) level = ancIsParent ? 2 : 1;
                        else if (t != mappedAnc && TransformUtilities.IsUnderRoot(mappedAnc, t)) level = 1;
                    }
                    float rank = raw + level * 0.25f;
                    if (rank > bestRank || (rank == bestRank && level > bestLevel))
                    {
                        bestRank = rank; bestRaw = raw; bestLevel = level; best = t;
                    }
                }

                if (best)
                {
                    map[s] = best;
                    if (scores != null) scores[s] = bestRaw;
                }
            }
            return map;
        }

        // ===== ペースト（コピー実行） =====

        static Transform GetRowTarget(MappingRow r, bool preferMA)
            => (preferMA && r.isMAExternal) ? r.maRootTarget : r.suggestedTarget;

        static bool UseAnchor(MappingRow r, bool preferMA)
            => preferMA && r.isMAExternal && r.maRootTarget;

        static Transform GetComponentRoot(Component c)
        {
            switch (c)
            {
                case VRCPhysBone pb: return pb.rootTransform;
                case VRCPhysBoneCollider col: return col.rootTransform;
                default: return null;
            }
        }

        /// <summary>行のターゲットが対応すべきソース側ボーン（rootTransform、未設定なら PB/Collider の付いている Transform）</summary>
        static Transform GetKeyBone(MappingRow r)
        {
            // Unity の fake null 対策で ?? は使わない
            var root = GetComponentRoot(r.sourceComponent);
            return root ? root : r.sourceTransform;
        }

        public static void ApplyCopy(
            List<MappingRow> rows,
            GameObject sourcePrefabRoot,
            GameObject targetPrefabRoot,
            Transform sourceArmatureRoot,
            Transform targetArmatureRoot,
            List<Transform> sourceAll,
            List<Transform> targetAll,
            bool copyOtherComponents,
            bool cloneExternalPBGameObject,
            bool preferMABoneProxyRootForExternal,
            bool enableHighlight = true)
        {
            if (!sourcePrefabRoot || !targetPrefabRoot) return;
            var srcRoot = sourcePrefabRoot.transform;
            var dstRoot = targetPrefabRoot.transform;
            bool preferMA = preferMABoneProxyRootForExternal;

            if (enableHighlight) PBMapperHooks.ClearAllHighlights?.Invoke();

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("PhysBone Mapper: Paste");

            var ctx = new RemapContext { sourceRoot = srcRoot };
            int pbCount = 0, colCount = 0, otherCount = 0;

            try
            {
                var applied = rows.Where(r => r != null && r.apply && r.sourceComponent && r.sourceTransform && GetRowTarget(r, preferMA)).ToList();

                // --- 対応表: UIで確定したターゲットを seed にして、配下のボーンもそれに追従させる
                var seeds = RootSeeds(srcRoot, dstRoot, sourceArmatureRoot, targetArmatureRoot).ToList();
                foreach (var r in applied)
                {
                    var key = UseAnchor(r, preferMA) ? r.maRootHint : GetKeyBone(r);
                    if (key && TransformUtilities.IsUnderRoot(sourceArmatureRoot, key))
                        seeds.Add(new KeyValuePair<Transform, Transform>(key, GetRowTarget(r, preferMA)));
                }
                ctx.transformMap = BuildTransformMap(sourceAll, targetAll, seeds, ctx.scores);

                var slotCounter = new Dictionary<(Transform, Type), int>();
                var remapTargets = new List<Component>();
                var forcedRoots = new List<KeyValuePair<Component, Transform>>();
                var maUpdates = new List<KeyValuePair<Transform, Transform>>();
                var siblingDone = new HashSet<(Transform, Transform)>();
                Transform lastHolder = null;

                // 1) Collider → 2) PhysBone の順（PB の colliders 参照を新しい Collider に繋ぐため）
                foreach (var kind in new[] { MappingRow.Kind.Collider, MappingRow.Kind.PhysBone })
                {
                    foreach (var r in applied.Where(x => x.kind == kind))
                    {
                        bool isPB = kind == MappingRow.Kind.PhysBone;
                        var target = GetRowTarget(r, preferMA);
                        bool anchor = UseAnchor(r, preferMA);

                        Transform dstHolder = anchor
                            ? PrepareDestinationHolderWithAnchor(r.sourceTransform, r.maRootTarget, isPB,
                                sourceArmatureRoot, cloneExternalPBGameObject, srcRoot, ctx.transformMap)
                            : PrepareDestinationHolder(r.sourceTransform, target, isPB,
                                sourceArmatureRoot, cloneExternalPBGameObject, srcRoot, dstRoot, ctx.transformMap);
                        if (!dstHolder) continue;

                        bool holderExternal = !TransformUtilities.IsUnderRoot(sourceArmatureRoot, r.sourceTransform);

                        // Armature 外 PB を GameObject ごと複製する場合、揺らすボーン（rootTransform 以下）も複製する
                        if (isPB && holderExternal && cloneExternalPBGameObject)
                        {
                            var effRoot = GetKeyBone(r);
                            if (!TransformUtilities.IsUnderRoot(sourceArmatureRoot, effRoot) && TransformUtilities.IsUnderRoot(srcRoot, effRoot))
                            {
                                var chainDst = anchor ? r.maRootTarget : dstRoot;
                                var clonedRoot = CloneExternalChainUnderTargetRoot(effRoot, srcRoot, chainDst, sourceArmatureRoot, ctx.transformMap);
                                CloneSubtree(effRoot, clonedRoot, sourceArmatureRoot, ctx.transformMap);
                            }
                        }

                        Component dstComp = isPB
                            ? (Component)CopyComponentTo((VRCPhysBone)r.sourceComponent, dstHolder, slotCounter)
                            : CopyComponentTo((VRCPhysBoneCollider)r.sourceComponent, dstHolder, slotCounter);
                        if (!dstComp) continue;

                        if (isPB) pbCount++; else colCount++;
                        ctx.componentMap[r.sourceComponent] = dstComp;
                        remapTargets.Add(dstComp);

                        if (!ctx.transformMap.ContainsKey(r.sourceTransform))
                            ctx.transformMap[r.sourceTransform] = dstHolder;

                        // rootTransform がボーンを指していた場合、UI で指定したターゲットを必ず使う
                        var srcCompRoot = GetComponentRoot(r.sourceComponent);
                        if (!anchor && srcCompRoot && TransformUtilities.IsUnderRoot(sourceArmatureRoot, srcCompRoot))
                            forcedRoots.Add(new KeyValuePair<Component, Transform>(dstComp, target));

                        // PB/Collider 以外のコンポーネント（Contact / Constraint / MA など）
                        if (isPB && copyOtherComponents && siblingDone.Add((r.sourceTransform, dstHolder)))
                        {
                            foreach (var pair in CopySiblingComponents(r.sourceTransform, dstHolder))
                            {
                                ctx.componentMap[pair.Key] = pair.Value;
                                remapTargets.Add(pair.Value);
                                otherCount++;
                            }
                        }

                        if (r.isMAExternal && r.maRootTarget)
                            maUpdates.Add(new KeyValuePair<Transform, Transform>(dstHolder, r.maRootTarget));

                        if (enableHighlight) PBMapperHooks.Highlight?.Invoke(dstHolder.gameObject, isPB);
                        lastHolder = dstHolder;
                    }
                }

                // 3) 一括リマップ（コピーした全コンポーネントの参照をターゲット側へ）
                foreach (var c in remapTargets)
                    RemapAllObjectReferences(c, ctx);

                foreach (var kv in forcedRoots)
                    SetObjectReference(kv.Key, "rootTransform", kv.Value);

                // 4) MA BoneProxy のターゲット更新（リマップ後に行い上書きされないようにする）
                foreach (var kv in maUpdates)
                    PBMapperHooks.UpdateMABoneProxyRootLocal?.Invoke(kv.Key, kv.Value);

                if (lastHolder) EditorGUIUtility.PingObject(lastHolder);
            }
            finally
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            // --- レポート
            if (ctx.lowConfidence.Count > 0)
                Debug.LogWarning("[PhysBoneMapper] 名前の一致度が低い繋ぎ替え（確認してください）:\n" + string.Join("\n", ctx.lowConfidence));
            if (ctx.unresolved.Count > 0)
                Debug.LogWarning("[PhysBoneMapper] ターゲット側に対応が見つからず None にした参照:\n" + string.Join("\n", ctx.unresolved));

            var msg = new StringBuilder();
            msg.AppendLine("Paste Completed");
            msg.AppendLine($"PhysBone: {pbCount} / Collider: {colCount}" + (copyOtherComponents ? $" / その他: {otherCount}" : ""));
            if (ctx.lowConfidence.Count > 0 || ctx.unresolved.Count > 0)
                msg.AppendLine($"要確認の参照: 低一致 {ctx.lowConfidence.Count} 件 / 未解決 {ctx.unresolved.Count} 件（Console 参照）");
            EditorUtility.DisplayDialog("PhysBone Mapper", msg.ToString(), "OK");
        }

        static T CopyComponentTo<T>(T src, Transform dstHolder, Dictionary<(Transform, Type), int> slotCounter) where T : Component
        {
            if (!src || !dstHolder) return null;
            var (dst, created) = MapOrAddComponent(src, dstHolder, slotCounter);
            if (!dst) return null;
            if (!created) Undo.RecordObject(dst, $"Paste {typeof(T).Name}");
            EditorUtility.CopySerialized(src, dst);
            PrefabUtility.RecordPrefabInstancePropertyModifications(dst);
            return dst;
        }

        static void SetObjectReference(Component c, string propertyName, Object value)
        {
            if (!c) return;
            var so = new SerializedObject(c);
            var sp = so.FindProperty(propertyName);
            if (sp == null || sp.propertyType != SerializedPropertyType.ObjectReference) return;
            if (sp.objectReferenceValue == value) return;
            sp.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ===== 参照リマップ =====

        /// <summary>互換用：名前ベースの対応表（seed なし）</summary>
        public static Dictionary<Transform, Transform> BuildTransformMap(List<Transform> src, List<Transform> dst)
            => BuildTransformMap(src, dst, null, null);

        /// <summary>
        /// コンポーネント内の全 ObjectReference を走査し、ソース Prefab 内を指す参照をターゲット側へ繋ぎ替える。
        /// Transform / GameObject / 任意の Component（Collider, Contact, Constraint 等）に対応。
        /// 対応先が無い参照は None にする（ソース素体への参照が残らないように）。
        /// </summary>
        public static void RemapAllObjectReferences(Component target, RemapContext ctx)
        {
            if (!target || ctx == null) return;
            var so = new SerializedObject(target);
            var it = so.GetIterator();
            var toRemove = new List<KeyValuePair<string, int>>();
            bool changed = false;
            bool compactArrays = target is VRCPhysBone || target is VRCPhysBoneCollider;

            while (it.NextVisible(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (it.name == "m_Script") continue;
                var obj = it.objectReferenceValue;
                if (!obj) continue;
                if (!TryResolve(obj, ctx, out var resolved)) continue;
                if (resolved == obj) continue;

                it.objectReferenceValue = resolved;
                changed = true;

                if (!resolved)
                {
                    ctx.unresolved.Add($"{target.transform.GetHierarchyPath()} [{target.GetType().Name}] {it.propertyPath} : {Describe(obj)}");
                    // PB の colliders / ignoreTransforms などのリストからは空要素を取り除く
                    if (compactArrays && TryParseArrayElement(it.propertyPath, out var arrayPath, out var index))
                        toRemove.Add(new KeyValuePair<string, int>(arrayPath, index));
                }
            }

            foreach (var e in toRemove.OrderByDescending(x => x.Value))
            {
                var arr = so.FindProperty(e.Key);
                if (arr == null || !arr.isArray || e.Value >= arr.arraySize) continue;
                var el = arr.GetArrayElementAtIndex(e.Value);
                if (el.propertyType == SerializedPropertyType.ObjectReference && el.objectReferenceValue == null)
                    arr.DeleteArrayElementAtIndex(e.Value);
            }

            if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>互換用オーバーロード</summary>
        public static void RemapAllObjectReferences(SerializedObject so,
            Dictionary<VRCPhysBoneCollider, VRCPhysBoneCollider> cmap,
            Dictionary<Transform, Transform> transformMap,
            Transform sourcePrefabRoot)
        {
            if (so == null) return;
            var ctx = new RemapContext { transformMap = transformMap ?? new Dictionary<Transform, Transform>(), sourceRoot = sourcePrefabRoot };
            if (cmap != null) foreach (var kv in cmap) ctx.componentMap[kv.Key] = kv.Value;
            foreach (var o in so.targetObjects)
                if (o is Component c) RemapAllObjectReferences(c, ctx);
            so.Update();
        }

        /// <summary>
        /// obj がソース Prefab 内のシーンオブジェクトなら true を返し、resolved に繋ぎ替え先（無ければ null）を入れる。
        /// ソース外（ターゲット側・アセット・他オブジェクト）の参照なら false（触らない）。
        /// </summary>
        static bool TryResolve(Object obj, RemapContext ctx, out Object resolved)
        {
            resolved = obj;
            var srcRoot = ctx.sourceRoot;
            switch (obj)
            {
                case Transform tr:
                    {
                        if (ctx.transformMap.TryGetValue(tr, out var dst) && dst)
                        { ctx.NoteMapped(tr, dst); resolved = dst; return true; }
                        if (TransformUtilities.IsUnderRoot(srcRoot, tr)) { resolved = null; return true; }
                        return false;
                    }
                case GameObject go:
                    {
                        var tr = go.transform;
                        if (ctx.transformMap.TryGetValue(tr, out var dst) && dst)
                        { ctx.NoteMapped(tr, dst); resolved = dst.gameObject; return true; }
                        if (TransformUtilities.IsUnderRoot(srcRoot, tr)) { resolved = null; return true; }
                        return false;
                    }
                case Component c:
                    {
                        if (ctx.componentMap.TryGetValue(c, out var mapped) && mapped)
                        { resolved = mapped; return true; }
                        var tr = c.transform;
                        if (!TransformUtilities.IsUnderRoot(srcRoot, tr)) return false;

                        resolved = null;
                        if (ctx.transformMap.TryGetValue(tr, out var dstT) && dstT)
                        {
                            var type = c.GetType();
                            var srcCands = tr.GetComponents(type).Where(x => x.GetType() == type).ToArray();
                            var dstCands = dstT.GetComponents(type).Where(x => x.GetType() == type).ToArray();
                            int index = Array.IndexOf(srcCands, c);
                            if (index >= 0 && index < dstCands.Length) resolved = dstCands[index];
                            else if (c is VRCPhysBoneCollider && dstCands.Length > 0) resolved = dstCands[0];
                            if (resolved) ctx.NoteMapped(tr, dstT);
                        }
                        return true;
                    }
                default:
                    return false;
            }
        }

        static string Describe(Object obj)
        {
            switch (obj)
            {
                case Transform t: return t.GetHierarchyPath();
                case GameObject g: return g.transform.GetHierarchyPath();
                case Component c: return $"{c.transform.GetHierarchyPath()} ({c.GetType().Name})";
                default: return obj ? obj.name : "(null)";
            }
        }

        static bool TryParseArrayElement(string propertyPath, out string arrayPath, out int index)
        {
            arrayPath = null; index = -1;
            const string marker = ".Array.data[";
            if (string.IsNullOrEmpty(propertyPath) || !propertyPath.EndsWith("]")) return false;
            int p = propertyPath.LastIndexOf(marker, StringComparison.Ordinal);
            if (p < 0) return false;
            int start = p + marker.Length;
            if (!int.TryParse(propertyPath.Substring(start, propertyPath.Length - start - 1), out index)) return false;
            arrayPath = propertyPath.Substring(0, p);
            return true;
        }

        // ===== 外部PB対応 =====

        public static Transform PrepareDestinationHolder(
            Transform srcHolder, Transform suggestedDstBone, bool isPhysBone,
            Transform sourceArmatureRoot, bool cloneExternalPBGameObject,
            Transform sourcePrefabRoot, Transform targetPrefabRoot,
            Dictionary<Transform, Transform> transformMap)
        {
            bool srcUnderArmature = TransformUtilities.IsUnderRoot(sourceArmatureRoot, srcHolder);
            if (srcUnderArmature) return suggestedDstBone;
            if (cloneExternalPBGameObject)
                return CloneExternalChainUnderTargetRoot(srcHolder, sourcePrefabRoot, targetPrefabRoot, sourceArmatureRoot, transformMap);
            if (!suggestedDstBone) return null;
            var container = GetOrCreateChild(suggestedDstBone, srcHolder, "Create PB Container");
            transformMap[srcHolder] = container;
            return container;
        }

        public static Transform PrepareDestinationHolderWithAnchor(
            Transform srcHolder, Transform targetAnchor, bool isPhysBone,
            Transform sourceArmatureRoot, bool cloneExternalPBGameObject,
            Transform sourcePrefabRoot,
            Dictionary<Transform, Transform> transformMap)
        {
            if (!targetAnchor) return null;
            if (TransformUtilities.IsUnderRoot(sourceArmatureRoot, srcHolder)) return targetAnchor;
            if (cloneExternalPBGameObject)
                return CloneExternalChainUnderTargetRoot(srcHolder, sourcePrefabRoot, targetAnchor, sourceArmatureRoot, transformMap);
            var container = GetOrCreateChild(targetAnchor, srcHolder, "Create PB Container (Anchor)");
            transformMap[srcHolder] = container;
            return container;
        }

        /// <summary>parent 直下に src と同名の子があれば再利用、なければ作成（再 Paste で重複させない）</summary>
        static Transform GetOrCreateChild(Transform parent, Transform src, string undoName)
        {
            var existing = TransformUtilities.FindChildByName(parent, src.name);
            if (existing) return existing;
            var go = new GameObject(src.name);
            go.transform.SetParent(parent, false);
            TransformUtilities.CopyLocalTRS(src, go.transform);
            Undo.RegisterCreatedObjectUndo(go, undoName);
            return go.transform;
        }

        public static Transform CloneExternalChainUnderTargetRoot(
            Transform srcLeaf, Transform srcRoot, Transform dstRoot,
            Transform sourceArmatureRoot,
            Dictionary<Transform, Transform> transformMap)
        {
            var stack = new Stack<Transform>();
            var cur = srcLeaf;
            while (cur != null && cur != srcRoot && !TransformUtilities.IsUnderRoot(sourceArmatureRoot, cur))
            {
                stack.Push(cur);
                cur = cur.parent;
            }
            Transform parent = dstRoot;
            Transform last = null;
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                var existing = GetOrCreateChild(parent, s, "Clone External PB Chain");
                transformMap[s] = existing;
                parent = existing;
                last = existing;
            }
            return last != null ? last : dstRoot;
        }

        /// <summary>src の子孫（Armature 外のもの）を dst 以下に Transform 構造だけ複製し、対応表に登録する</summary>
        public static void CloneSubtree(Transform src, Transform dst, Transform sourceArmatureRoot, Dictionary<Transform, Transform> transformMap)
        {
            if (!src || !dst) return;
            for (int i = 0; i < src.childCount; i++)
            {
                var c = src.GetChild(i);
                if (TransformUtilities.IsUnderRoot(sourceArmatureRoot, c)) continue;
                var d = GetOrCreateChild(dst, c, "Clone External PB Bones");
                transformMap[c] = d;
                CloneSubtree(c, d, sourceArmatureRoot, transformMap);
            }
        }

        /// <summary>
        /// PB/Collider 以外のコンポーネントをコピーし、(ソース, 作成先) の組を返す。
        /// 戻り値は呼び出し側で参照リマップの対象に加えること（Issue #4）。
        /// ターゲットに同種コンポーネントが既にある分は上書きしない。
        /// </summary>
        public static List<KeyValuePair<Component, Component>> CopySiblingComponents(Transform src, Transform dst)
        {
            var result = new List<KeyValuePair<Component, Component>>();
            if (!src || !dst || src == dst) return result;

            var srcIndexByType = new Dictionary<Type, int>();
            var dstCountByType = new Dictionary<Type, int>();

            foreach (var comp in src.GetComponents<Component>())
            {
                if (comp == null) continue; // Missing Script
                var tp = comp.GetType();
                if (comp is Transform) continue;
                if (comp is VRCPhysBone || comp is VRCPhysBoneCollider) continue;

                srcIndexByType.TryGetValue(tp, out int idx);
                srcIndexByType[tp] = idx + 1;
                if (!dstCountByType.TryGetValue(tp, out int existing))
                {
                    existing = dst.GetComponents(tp).Count(x => x && x.GetType() == tp);
                    dstCountByType[tp] = existing;
                }
                if (idx < existing) continue;

                Component newC = null;
                try
                {
                    newC = Undo.AddComponent(dst.gameObject, tp);
                    if (!newC) continue;
                    EditorUtility.CopySerialized(comp, newC);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(newC);
                    result.Add(new KeyValuePair<Component, Component>(comp, newC));
                }
                catch (Exception e)
                {
                    if (newC) Undo.DestroyObjectImmediate(newC);
                    Debug.LogWarning($"[PhysBoneMapper] コンポーネント {tp.Name} のコピーに失敗: {e.Message}");
                }
            }
            return result;
        }

        /// <summary>
        /// dstHolder 上の既存コンポーネントを再利用するか、新規追加する。
        /// slotCounter を渡すと「同じ dstHolder に何個目に貼るか」で既存を選ぶため、
        /// rootTransform 側に貼る場合や再 Paste でも重複しない。
        /// </summary>
        public static (T component, bool created) MapOrAddComponent<T>(
            T srcComp, Transform dstHolder,
            Dictionary<(Transform, Type), int> slotCounter = null) where T : Component
        {
            if (srcComp == null) throw new ArgumentNullException(nameof(srcComp));
            if (dstHolder == null) throw new ArgumentNullException(nameof(dstHolder));

            int index;
            if (slotCounter != null)
            {
                var key = (dstHolder, typeof(T));
                slotCounter.TryGetValue(key, out index);
                slotCounter[key] = index + 1;
            }
            else
            {
                index = Array.IndexOf(srcComp.gameObject.GetComponents<T>(), srcComp);
            }

            var dstAll = dstHolder.gameObject.GetComponents<T>();
            if (index >= 0 && index < dstAll.Length) return (dstAll[index], false);
            var newComp = Undo.AddComponent<T>(dstHolder.gameObject);
            return (newComp, true);
        }
    }
}
#endif
