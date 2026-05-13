#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

using VRC.SDK3.Dynamics.PhysBone.Components;

namespace PBMapper
{
    public static class PhysBoneMapperEngine
    {
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

            foreach (var t in sourceAll)
            {
                // === PhysBone ===
                foreach (var pb in t.GetComponents<VRCPhysBone>())
                {
                    bool hasLocalMA = PBMapperHooks.HasLocalMABoneProxy?.Invoke(t) ?? false;
                    bool isOutside = sourceArmatureRoot && !TransformUtilities.IsUnderRoot(sourceArmatureRoot, t);
                    Transform proxyRoot = null;
                    if (preferMABoneProxyRootForExternal && isOutside)
                        proxyRoot = PBMapperHooks.TryGetMABoneProxyRoot?.Invoke(t);

                    Transform cand; float score;
                    if (proxyRoot)
                    {
                        (cand, score) = SuggestTargetByNameInSubtree(targetArmatureRoot, proxyRoot.name);
                    }
                    else
                    {
                        (cand, score) = SuggestTargetForPhysBone(pb, sourceArmatureRoot, targetArmatureRoot, targetAll, preferMABoneProxyRootForExternal);
                    }

                    rows.Add(new MappingRow
                    {
                        kind = MappingRow.Kind.PhysBone,
                        kindLabel = hasLocalMA ? "MA/PhysBone" : "PhysBone",
                        sourceComponent = pb,
                        sourceTransform = t,
                        suggestedTarget = cand,
                        isMAExternal = proxyRoot,
                        maRootHint = proxyRoot,
                        maRootTarget = proxyRoot ? cand : null,
                        score = score,
                        info = proxyRoot ? $"{score:F2} (via:{proxyRoot.name})" : $"{score:F2}"
                    });
                }

                // === Colliders ===
                foreach (var col in t.GetComponents<VRCPhysBoneCollider>())
                {
                    bool hasLocalMA = PBMapperHooks.HasLocalMABoneProxy?.Invoke(t) ?? false;
                    bool isOutside = sourceArmatureRoot && !TransformUtilities.IsUnderRoot(sourceArmatureRoot, t);
                    Transform proxyRoot = null;
                    if (preferMABoneProxyRootForExternal && isOutside)
                        proxyRoot = PBMapperHooks.TryGetMABoneProxyRoot?.Invoke(t);

                    Transform cand; float score;
                    if (proxyRoot)
                    {
                        (cand, score) = SuggestTargetByNameInSubtree(targetArmatureRoot, proxyRoot.name);
                    }
                    else
                    {
                        (cand, score) = SuggestTargetForCollider(col, targetAll);
                    }

                    rows.Add(new MappingRow
                    {
                        kind = MappingRow.Kind.Collider,
                        kindLabel = hasLocalMA ? "MA/Collider" : "Collider",
                        sourceComponent = col,
                        sourceTransform = t,
                        suggestedTarget = cand,
                        isMAExternal = proxyRoot,
                        maRootHint = proxyRoot,
                        maRootTarget = proxyRoot ? cand : null,
                        score = score,
                        info = proxyRoot ? $"{score:F2} (via:{proxyRoot.name})" : $"{score:F2}"
                    });
                }
            }
        }

        // ===== サジェスト =====

        public static (Transform, float) SuggestTargetForPhysBone(
            VRCPhysBone srcPb,
            Transform sourceArmatureRoot,
            Transform targetArmatureRoot,
            List<Transform> targetAll,
            bool preferMABoneProxyRootForExternal)
        {
            if (!srcPb) return (null, 0);
            if (preferMABoneProxyRootForExternal && sourceArmatureRoot && !TransformUtilities.IsUnderRoot(sourceArmatureRoot, srcPb.transform))
            {
                var proxyRoot = PBMapperHooks.TryGetMABoneProxyRoot?.Invoke(srcPb.transform);
                if (proxyRoot) return SuggestTargetByRootFirst(
                    srcPb.rootTransform ? srcPb.rootTransform.name : srcPb.transform.name,
                    proxyRoot, targetArmatureRoot, targetAll);
            }
            if (srcPb.rootTransform) return SuggestTargetByName(srcPb.rootTransform.name, targetAll);
            return SuggestTargetByName(srcPb.transform.name, targetAll);
        }

        public static (Transform, float) SuggestTargetForCollider(
            VRCPhysBoneCollider srcCol,
            List<Transform> targetAll)
        {
            if (!srcCol) return (null, 0);
            if (srcCol.rootTransform) return SuggestTargetByName(srcCol.rootTransform.name, targetAll);
            return SuggestTargetByName(srcCol.transform.name, targetAll);
        }

        public static (Transform, float) SuggestTargetByRootFirst(
            string leafSourceName, Transform maRootHint,
            Transform targetArmatureRoot, List<Transform> targetAll)
        {
            if (maRootHint && targetArmatureRoot)
            {
                var (targetRoot, sRoot) = SuggestTargetByNameInSubtree(targetArmatureRoot, maRootHint.name);
                if (targetRoot)
                {
                    var (leaf, sLeaf) = SuggestTargetByNameInSubtree(targetRoot, leafSourceName);
                    if (leaf)
                    {
                        float score = Mathf.Clamp01(sRoot * 0.7f + sLeaf * 0.5f);
                        return (score >= 0.6f ? leaf : null, score);
                    }
                    float rootScore = Mathf.Clamp01(sRoot * 0.7f);
                    return (rootScore >= 0.6f ? targetRoot : null, rootScore);
                }
            }
            return SuggestTargetByName(leafSourceName, targetAll);
        }

        public static (Transform, float) SuggestTargetByName(string srcName, List<Transform> targetAll)
        {
            if (string.IsNullOrEmpty(srcName)) return (null, 0);
            float bestScore = float.NegativeInfinity; Transform best = null;
            foreach (var t in targetAll)
            {
                float score = FuzzyMatcher.MatchScore(srcName, t.name);
                if (score > bestScore) { bestScore = score; best = t; }
            }
            return (bestScore >= 0.6f ? best : null, bestScore);
        }

        public static (Transform, float) SuggestTargetByNameInSubtree(Transform searchRoot, string srcName)
        {
            if (!searchRoot || string.IsNullOrEmpty(srcName)) return (null, 0);
            float bestScore = float.NegativeInfinity; Transform best = null;
            foreach (var t in searchRoot.GetComponentsInChildren<Transform>(true))
            {
                float score = FuzzyMatcher.MatchScore(srcName, t.name);
                if (score > bestScore) { bestScore = score; best = t; }
            }
            return (bestScore >= 0.6f ? best : null, bestScore);
        }

        // ===== ペースト（コピー実行） =====

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
            if (!targetPrefabRoot) return;

            var colliderMap = new Dictionary<VRCPhysBoneCollider, VRCPhysBoneCollider>();
            var transformMap = BuildTransformMap(sourceAll, targetAll);

            if (enableHighlight) PBMapperHooks.ClearAllHighlights?.Invoke();

            var remapTargets = new List<SerializedObject>(rows.Count);

            // 1) Colliders 先にコピー
            foreach (var r in rows.Where(x => x.apply && x.kind == MappingRow.Kind.Collider && x.suggestedTarget))
            {
                var srcCol = r.sourceComponent as VRCPhysBoneCollider; if (!srcCol) continue;

                Transform dstHolder = (preferMABoneProxyRootForExternal && r.isMAExternal && r.maRootTarget)
                    ? PrepareDestinationHolderWithAnchor(r.sourceTransform, r.maRootTarget, false,
                        sourceArmatureRoot, cloneExternalPBGameObject, sourcePrefabRoot.transform, transformMap)
                    : PrepareDestinationHolder(r.sourceTransform, r.suggestedTarget, false,
                        sourceArmatureRoot, cloneExternalPBGameObject, sourcePrefabRoot.transform, targetPrefabRoot.transform, transformMap);

                var (dstCol, colCreated) = MapOrAddComponent<VRCPhysBoneCollider>(srcCol, dstHolder);
                if (!colCreated) Undo.RecordObject(dstCol, "Paste VRCPhysBoneCollider");
                EditorUtility.CopySerialized(srcCol, dstCol);

                if (!transformMap.ContainsKey(r.sourceTransform))
                    transformMap[r.sourceTransform] = dstHolder;
                colliderMap[srcCol] = dstCol;
                remapTargets.Add(new SerializedObject(dstCol));

                if (r.isMAExternal && r.maRootTarget)
                    PBMapperHooks.UpdateMABoneProxyRootLocal?.Invoke(dstHolder, r.maRootTarget);

                if (enableHighlight) PBMapperHooks.Highlight?.Invoke(dstHolder.gameObject, false);
                EditorGUIUtility.PingObject(dstHolder);
            }

            // 2) PhysBones
            foreach (var r in rows.Where(x => x.apply && x.kind == MappingRow.Kind.PhysBone && x.suggestedTarget))
            {
                var srcPb = r.sourceComponent as VRCPhysBone; if (!srcPb) continue;

                Transform dstHolder = (preferMABoneProxyRootForExternal && r.isMAExternal && r.maRootTarget)
                    ? PrepareDestinationHolderWithAnchor(r.sourceTransform, r.maRootTarget, true,
                        sourceArmatureRoot, cloneExternalPBGameObject, sourcePrefabRoot.transform, transformMap)
                    : PrepareDestinationHolder(r.sourceTransform, r.suggestedTarget, true,
                        sourceArmatureRoot, cloneExternalPBGameObject, sourcePrefabRoot.transform, targetPrefabRoot.transform, transformMap);

                var (dstPb, pbCreated) = MapOrAddComponent<VRCPhysBone>(srcPb, dstHolder);
                if (!pbCreated) Undo.RecordObject(dstPb, "Paste VRCPhysBone");
                EditorUtility.CopySerialized(srcPb, dstPb);

                if (!transformMap.ContainsKey(r.sourceTransform))
                    transformMap[r.sourceTransform] = dstHolder;
                remapTargets.Add(new SerializedObject(dstPb));

                if (copyOtherComponents)
                    CopySiblingComponents(r.sourceTransform, dstHolder);

                if (r.isMAExternal && r.maRootTarget)
                    PBMapperHooks.UpdateMABoneProxyRootLocal?.Invoke(dstHolder, r.maRootTarget);

                if (enableHighlight) PBMapperHooks.Highlight?.Invoke(dstHolder.gameObject, true);
                EditorGUIUtility.PingObject(dstHolder);
            }

            // 3) 一括リマップ
            foreach (var so in remapTargets)
            {
                RemapAllObjectReferences(so, colliderMap, transformMap, sourcePrefabRoot.transform);
            }

            EditorUtility.DisplayDialog("PhysBone Mapper", "Paste Completed", "OK");
        }

        // ===== 参照リマップ =====

        public static Dictionary<Transform, Transform> BuildTransformMap(List<Transform> src, List<Transform> dst)
        {
            var map = new Dictionary<Transform, Transform>();
            foreach (var s in src)
            {
                if (s == null) continue;
                if (string.IsNullOrEmpty(FuzzyMatcher.NormalizeKey(s.name))) continue;
                Transform best = null; float bestScore = float.NegativeInfinity;
                foreach (var t in dst)
                {
                    if (t == null) continue;
                    float score = FuzzyMatcher.MatchScore(s.name, t.name);
                    if (score > bestScore) 
                    { 
                        bestScore = score; best = t; 
                    }
                    else if (score == bestScore && bestScore > 0f)
                    {
                        bool curAncestryMatch = (s.parent && t.parent && map.TryGetValue(s.parent, out var mappedParent) && mappedParent == t.parent);
                        bool bestAncestryMatch = (s.parent && best.parent && map.TryGetValue(s.parent, out var mappedParentBest) && mappedParentBest == best.parent);
                        if (curAncestryMatch && !bestAncestryMatch)
                        {
                            best = t;
                        }
                    }
                }
                if (best && bestScore >= 0.6f) map[s] = best;
            }
            return map;
        }



        public static void RemapAllObjectReferences(SerializedObject so,
            Dictionary<VRCPhysBoneCollider, VRCPhysBoneCollider> cmap,
            Dictionary<Transform, Transform> transformMap,
            Transform sourcePrefabRoot)
        {
            if (so == null) return;
            Debug.Assert(sourcePrefabRoot != null, "[PhysBoneMapper] sourcePrefabRoot is null in RemapAllObjectReferences");
            var it = so.GetIterator();
            while (it.NextVisible(true))
            {
                if (it.propertyType == SerializedPropertyType.ObjectReference)
                {
                    var obj = it.objectReferenceValue;
                    switch (obj)
                    {
                        case Transform tr:
                            {
                                if (tr && transformMap.TryGetValue(tr, out var dst))
                                    it.objectReferenceValue = dst;
                                else if (tr && TransformUtilities.IsUnderRoot(sourcePrefabRoot, tr))
                                    it.objectReferenceValue = null;
                                break;
                            }
                        case VRCPhysBoneCollider col:
                            {
                                if (col && cmap.TryGetValue(col, out var mapped))
                                    it.objectReferenceValue = mapped;
                                else if (col && col.transform && transformMap.TryGetValue(col.transform, out var tf))
                                {
                                    var dstCands = tf.GetComponents<VRCPhysBoneCollider>();
                                    if (dstCands.Length > 0)
                                    {
                                        var srcCands = col.transform.GetComponents<VRCPhysBoneCollider>();
                                        int index = Array.IndexOf(srcCands, col);
                                        it.objectReferenceValue = (index >= 0 && index < dstCands.Length) ? dstCands[index] : dstCands[0];
                                    }
                                }
                                else if (col && col.transform && TransformUtilities.IsUnderRoot(sourcePrefabRoot, col.transform))
                                    it.objectReferenceValue = null;
                                break;
                            }
                    }
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
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
            var container = new GameObject(srcHolder.name);
            container.transform.SetParent(suggestedDstBone, false);
            TransformUtilities.CopyLocalTRS(srcHolder, container.transform);
            Undo.RegisterCreatedObjectUndo(container.gameObject, "Create PB Container");
            transformMap[srcHolder] = container.transform;
            return container.transform;
        }

        public static Transform PrepareDestinationHolderWithAnchor(
            Transform srcHolder, Transform targetAnchor, bool isPhysBone,
            Transform sourceArmatureRoot, bool cloneExternalPBGameObject,
            Transform sourcePrefabRoot,
            Dictionary<Transform, Transform> transformMap)
        {
            if (TransformUtilities.IsUnderRoot(sourceArmatureRoot, srcHolder)) return targetAnchor;
            if (cloneExternalPBGameObject)
                return CloneExternalChainUnderTargetRoot(srcHolder, sourcePrefabRoot, targetAnchor, sourceArmatureRoot, transformMap);
            var container = new GameObject(srcHolder.name);
            container.transform.SetParent(targetAnchor, false);
            TransformUtilities.CopyLocalTRS(srcHolder, container.transform);
            Undo.RegisterCreatedObjectUndo(container.gameObject, "Create PB Container (Anchor)");
            transformMap[srcHolder] = container.transform;
            return container.transform;
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
                var existing = TransformUtilities.FindChildByName(parent, s.name);
                if (existing == null)
                {
                    var go = new GameObject(s.name);
                    go.transform.SetParent(parent, false);
                    TransformUtilities.CopyLocalTRS(s, go.transform);
                    Undo.RegisterCreatedObjectUndo(go, "Clone External PB Chain");
                    existing = go.transform;
                }
                transformMap[s] = existing;
                parent = existing;
                last = existing;
            }
            return last != null ? last : dstRoot;
        }

        public static void CopySiblingComponents(Transform src, Transform dst)
        {
            if (!src || !dst) return;
            foreach (var comp in src.GetComponents<Component>())
            {
                if (comp == null) continue; var tp = comp.GetType();
                if (tp == typeof(Transform)) continue;
                if (tp == typeof(VRCPhysBone) || tp == typeof(VRCPhysBoneCollider)) continue;
                if (dst.GetComponent(tp)) continue;
                Component newC = null;
                try { newC = dst.gameObject.AddComponent(tp); Undo.RegisterCreatedObjectUndo(newC, $"Copy {tp.Name}"); EditorUtility.CopySerialized(comp, newC); }
                catch (Exception e) { if (newC != null) Undo.DestroyObjectImmediate(newC); Debug.LogWarning($"[PhysBoneMapper] コンポーネント {tp.Name} のコピーに失敗: {e.Message}"); }
            }
        }

        public static void CopyAllComponents(Transform src, Transform dst)
        {
            if (!src || !dst) return;
            foreach (var comp in src.GetComponents<Component>())
            {
                if (comp == null) continue;
                var tp = comp.GetType();
                if (tp == typeof(Transform)) continue;
                if (dst.GetComponent(tp) != null) continue;
                Component newC = null;
                try { newC = dst.gameObject.AddComponent(tp); Undo.RegisterCreatedObjectUndo(newC, $"Copy {tp.Name}"); EditorUtility.CopySerialized(comp, newC); }
                catch (Exception e) { if (newC != null) Undo.DestroyObjectImmediate(newC); Debug.LogWarning($"[PhysBoneMapper] コンポーネント {tp.Name} のコピーに失敗: {e.Message}"); }
            }
        }

        public static (T component, bool created) MapOrAddComponent<T>(T srcComp, Transform dstHolder) where T : Component
        {
            if (srcComp == null) throw new ArgumentNullException(nameof(srcComp));
            var srcAll = srcComp.gameObject.GetComponents<T>();
            int index = Array.IndexOf(srcAll, srcComp);
            var dstAll = dstHolder.gameObject.GetComponents<T>();
            if (index >= 0 && index < dstAll.Length) return (dstAll[index], false);
            var newComp = dstHolder.gameObject.AddComponent<T>();
            Undo.RegisterCreatedObjectUndo(newComp, $"Add {typeof(T).Name}");
            return (newComp, true);
        }
    }
}
#endif
