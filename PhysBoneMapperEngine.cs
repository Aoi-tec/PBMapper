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
                        return (leaf, score);
                    }
                    return (targetRoot, Mathf.Clamp01(sRoot * 0.7f));
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
            return (best, bestScore);
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
            return (best, bestScore);
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
            var transformMap = BuildTransformNameMap(sourceAll, targetAll);

            if (enableHighlight) PBMapperHooks.ClearAllHighlights?.Invoke();

            // 1) Colliders 先にコピー
            foreach (var r in rows.Where(x => x.apply && x.kind == MappingRow.Kind.Collider && x.suggestedTarget))
            {
                var srcCol = r.sourceComponent as VRCPhysBoneCollider; if (!srcCol) continue;

                Transform dstHolder = (preferMABoneProxyRootForExternal && r.isMAExternal && r.maRootTarget)
                    ? PrepareDestinationHolderWithAnchor(r.sourceTransform, r.maRootTarget, false,
                        sourceArmatureRoot, cloneExternalPBGameObject, sourcePrefabRoot.transform)
                    : PrepareDestinationHolder(r.sourceTransform, r.suggestedTarget, false,
                        sourceArmatureRoot, cloneExternalPBGameObject, sourcePrefabRoot.transform, targetPrefabRoot.transform);

                var dstCol = MapOrAddComponent<VRCPhysBoneCollider>(srcCol, dstHolder);
                EditorUtility.CopySerialized(srcCol, dstCol);

                RemapAllObjectReferences(new SerializedObject(dstCol), colliderMap, transformMap);

                colliderMap[srcCol] = dstCol;

                if (r.isMAExternal && r.maRootTarget)
                    PBMapperHooks.UpdateMABoneProxyRootLocal?.Invoke(dstHolder, r.maRootTarget);

                if (enableHighlight) PBMapperHooks.Highlight?.Invoke(dstHolder.gameObject, false);
                EditorGUIUtility.PingObject(dstHolder);
                Undo.RegisterCreatedObjectUndo(dstCol, "Paste VRCPhysBoneCollider");
            }

            // 2) PhysBones
            foreach (var r in rows.Where(x => x.apply && x.kind == MappingRow.Kind.PhysBone && x.suggestedTarget))
            {
                var srcPb = r.sourceComponent as VRCPhysBone; if (!srcPb) continue;

                Transform dstHolder = (preferMABoneProxyRootForExternal && r.isMAExternal && r.maRootTarget)
                    ? PrepareDestinationHolderWithAnchor(r.sourceTransform, r.maRootTarget, true,
                        sourceArmatureRoot, cloneExternalPBGameObject, sourcePrefabRoot.transform)
                    : PrepareDestinationHolder(r.sourceTransform, r.suggestedTarget, true,
                        sourceArmatureRoot, cloneExternalPBGameObject, sourcePrefabRoot.transform, targetPrefabRoot.transform);

                var dstPb = MapOrAddComponent<VRCPhysBone>(srcPb, dstHolder);
                EditorUtility.CopySerialized(srcPb, dstPb);

                var so = new SerializedObject(dstPb); so.Update();
                TryRemapTransformField(so, "rootTransform", srcPb.rootTransform, transformMap);
                TryRemapTransformArray(so, "ignoreTransforms", srcPb.ignoreTransforms.ToArray(), transformMap);
                TryRemapColliderArray(so, "colliders",
                    srcPb.colliders.OfType<VRCPhysBoneCollider>().ToArray(),
                    colliderMap, transformMap);
                so.ApplyModifiedPropertiesWithoutUndo();

                RemapAllObjectReferences(new SerializedObject(dstPb), colliderMap, transformMap);

                if (copyOtherComponents)
                    CopySiblingComponents(r.sourceTransform, dstHolder);

                if (r.isMAExternal && r.maRootTarget)
                    PBMapperHooks.UpdateMABoneProxyRootLocal?.Invoke(dstHolder, r.maRootTarget);

                if (enableHighlight) PBMapperHooks.Highlight?.Invoke(dstHolder.gameObject, true);
                EditorGUIUtility.PingObject(dstHolder);
                Undo.RegisterCreatedObjectUndo(dstPb, "Paste VRCPhysBone");
            }

            EditorUtility.DisplayDialog("PhysBone Mapper", "Paste Completed", "OK");
        }

        // ===== 参照リマップ =====

        public static Dictionary<string, Transform> BuildTransformNameMap(List<Transform> src, List<Transform> dst)
        {
            var map = new Dictionary<string, Transform>();
            foreach (var s in src)
            {
                var key = FuzzyMatcher.NormalizeKey(s.name); if (string.IsNullOrEmpty(key)) continue;
                if (map.ContainsKey(key)) continue;
                Transform best = null; float bestScore = float.NegativeInfinity;
                foreach (var t in dst)
                {
                    float score = FuzzyMatcher.MatchScore(s.name, t.name);
                    if (score > bestScore) { bestScore = score; best = t; }
                }
                if (best) map[key] = best;
            }
            return map;
        }

        public static void TryRemapTransformField(SerializedObject so, string propName, Transform src, Dictionary<string, Transform> nameMap)
        {
            if (so == null || src == null) return; var prop = so.FindProperty(propName); if (prop == null) return;
            var key = FuzzyMatcher.NormalizeKey(src.name); if (!string.IsNullOrEmpty(key) && nameMap.TryGetValue(key, out var dst)) prop.objectReferenceValue = dst;
        }

        public static void TryRemapTransformArray(SerializedObject so, string propName, Transform[] srcArr, Dictionary<string, Transform> nameMap)
        {
            if (so == null || srcArr == null) return; var prop = so.FindProperty(propName); if (prop == null) return;
            prop.arraySize = srcArr.Length;
            for (int i = 0; i < srcArr.Length; i++)
            {
                var src = srcArr[i]; Transform dst = null;
                if (src)
                {
                    var key = FuzzyMatcher.NormalizeKey(src.name);
                    if (!string.IsNullOrEmpty(key)) nameMap.TryGetValue(key, out dst);
                }
                prop.GetArrayElementAtIndex(i).objectReferenceValue = dst;
            }
        }

        public static void TryRemapColliderArray(SerializedObject so, string propName, VRCPhysBoneCollider[] srcArr,
            Dictionary<VRCPhysBoneCollider, VRCPhysBoneCollider> cmap,
            Dictionary<string, Transform> nameMap)
        {
            if (so == null || srcArr == null) return; var prop = so.FindProperty(propName); if (prop == null) return;
            prop.arraySize = srcArr.Length;
            for (int i = 0; i < srcArr.Length; i++)
            {
                var src = srcArr[i]; UnityEngine.Object dst = null;
                if (src && cmap.TryGetValue(src, out var mapped)) dst = mapped;
                else if (src && src.transform)
                {
                    var key = FuzzyMatcher.NormalizeKey(src.transform.name);
                    if (!string.IsNullOrEmpty(key) && nameMap.TryGetValue(key, out var tf))
                    {
                        var cands = tf.GetComponents<VRCPhysBoneCollider>();
                        if (cands.Length > 0) dst = cands[0];
                    }
                }
                prop.GetArrayElementAtIndex(i).objectReferenceValue = dst;
            }
        }

        public static void RemapAllObjectReferences(SerializedObject so,
            Dictionary<VRCPhysBoneCollider, VRCPhysBoneCollider> cmap,
            Dictionary<string, Transform> nameMap)
        {
            if (so == null) return;
            var it = so.GetIterator();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (it.propertyType == SerializedPropertyType.ObjectReference)
                {
                    var obj = it.objectReferenceValue;
                    switch (obj)
                    {
                        case Transform tr:
                            {
                                var key = FuzzyMatcher.NormalizeKey(tr.name);
                                if (!string.IsNullOrEmpty(key) && nameMap.TryGetValue(key, out var dst))
                                    it.objectReferenceValue = dst;
                                break;
                            }
                        case VRCPhysBoneCollider col:
                            {
                                if (cmap.TryGetValue(col, out var mapped)) it.objectReferenceValue = mapped;
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
            Transform sourcePrefabRoot, Transform targetPrefabRoot)
        {
            bool srcUnderArmature = TransformUtilities.IsUnderRoot(sourceArmatureRoot, srcHolder);
            if (srcUnderArmature) return suggestedDstBone;
            if (cloneExternalPBGameObject)
                return CloneExternalChainUnderTargetRoot(srcHolder, sourcePrefabRoot, targetPrefabRoot, sourceArmatureRoot);
            var container = new GameObject(srcHolder.name);
            container.transform.SetParent(suggestedDstBone, false);
            TransformUtilities.CopyLocalTRS(srcHolder, container.transform);
            Undo.RegisterCreatedObjectUndo(container.gameObject, "Create PB Container");
            return container.transform;
        }

        public static Transform PrepareDestinationHolderWithAnchor(
            Transform srcHolder, Transform targetAnchor, bool isPhysBone,
            Transform sourceArmatureRoot, bool cloneExternalPBGameObject,
            Transform sourcePrefabRoot)
        {
            if (TransformUtilities.IsUnderRoot(sourceArmatureRoot, srcHolder)) return targetAnchor;
            if (cloneExternalPBGameObject)
                return CloneExternalChainUnderTargetRoot(srcHolder, sourcePrefabRoot, targetAnchor, sourceArmatureRoot);
            var container = new GameObject(srcHolder.name);
            container.transform.SetParent(targetAnchor, false);
            TransformUtilities.CopyLocalTRS(srcHolder, container.transform);
            Undo.RegisterCreatedObjectUndo(container.gameObject, "Create PB Container (Anchor)");
            return container.transform;
        }

        public static Transform CloneExternalChainUnderTargetRoot(
            Transform srcLeaf, Transform srcRoot, Transform dstRoot,
            Transform sourceArmatureRoot)
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
                try { var newC = dst.gameObject.AddComponent(tp); EditorUtility.CopySerialized(comp, newC); Undo.RegisterCreatedObjectUndo(newC, $"Copy {tp.Name}"); }
                catch (Exception e) { Debug.LogWarning($"[PhysBoneMapper] コンポーネント {tp.Name} のコピーに失敗: {e.Message}"); }
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
                try { var newC = dst.gameObject.AddComponent(tp); EditorUtility.CopySerialized(comp, newC); Undo.RegisterCreatedObjectUndo(newC, $"Copy {tp.Name}"); }
                catch (Exception e) { Debug.LogWarning($"[PhysBoneMapper] コンポーネント {tp.Name} のコピーに失敗: {e.Message}"); }
            }
        }

        public static T MapOrAddComponent<T>(T srcComp, Transform dstHolder) where T : Component
        {
            var srcAll = srcComp.gameObject.GetComponents<T>();
            int index = Array.IndexOf(srcAll, srcComp);
            var dstAll = dstHolder.gameObject.GetComponents<T>();
            if (index >= 0 && index < dstAll.Length) return dstAll[index];
            return dstHolder.gameObject.AddComponent<T>();
        }
    }
}
#endif
