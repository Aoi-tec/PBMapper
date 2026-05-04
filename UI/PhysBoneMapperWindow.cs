#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PBMapper
{
    public class PhysBoneMapperWindow : EditorWindow
    {
        [MenuItem("Tools/Aoisan/PhysBone Mapper")]
        public static void Open() => GetWindow<PhysBoneMapperWindow>("PhysBone Mapper");

        // === 入力 ===
        private GameObject sourcePrefabRoot;
        private GameObject targetPrefabRoot;

        private Transform sourceArmatureRoot;
        private Transform targetArmatureRoot;

        // === 収集結果 ===
        private List<Transform> sourceAll = new();
        private List<Transform> targetAll = new();

        private readonly List<MappingRow> rows = new();

        // オプション
        private bool copyOtherComponents = true;
        private bool scopePrefabWide = true;
        private bool cloneExternalPBGameObject = true;
        private bool preferMABoneProxyRootForExternal = true;
        private bool enableHighlight = true;

        private Vector2 scroll;

        // ===== UI =====
        private void OnGUI()
        {
            EditorGUILayout.LabelField("PhysBone Mapper (Prefab-wide)", EditorStyles.boldLabel);
            sourcePrefabRoot = (GameObject)EditorGUILayout.ObjectField("Source Prefab Root", sourcePrefabRoot, typeof(GameObject), true);
            targetPrefabRoot = (GameObject)EditorGUILayout.ObjectField("Target Prefab Root", targetPrefabRoot, typeof(GameObject), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                scopePrefabWide = EditorGUILayout.ToggleLeft("探索範囲: Prefab全体", scopePrefabWide, GUILayout.Width(180));
                copyOtherComponents = EditorGUILayout.ToggleLeft("PB/Collider以外もコピー", copyOtherComponents, GUILayout.Width(200));
                cloneExternalPBGameObject = EditorGUILayout.ToggleLeft("Armature外PBはGameObjectごと複製", cloneExternalPBGameObject, GUILayout.Width(260));
                if (PBMapperHooks.IsProAvailable)
                    preferMABoneProxyRootForExternal = EditorGUILayout.ToggleLeft("Armature外は MABoneProxy の root でマッチ", preferMABoneProxyRootForExternal, GUILayout.Width(300));
            }

            if (PBMapperHooks.IsProAvailable)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool prev = enableHighlight;
                    enableHighlight = EditorGUILayout.ToggleLeft("Hierarchy色付け", enableHighlight, GUILayout.Width(150));
                    if (prev != enableHighlight)
                        PBMapperHooks.SetHighlightEnabled?.Invoke(enableHighlight);
                    if (GUILayout.Button("色付けクリア", GUILayout.Width(100)))
                        PBMapperHooks.ClearAllHighlights?.Invoke();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = sourcePrefabRoot && targetPrefabRoot;
                if (GUILayout.Button("Scan (候補生成)"))
                    PhysBoneMapperEngine.Scan(rows, sourcePrefabRoot, targetPrefabRoot,
                        ref sourceArmatureRoot, ref targetArmatureRoot,
                        ref sourceAll, ref targetAll,
                        scopePrefabWide,
                        PBMapperHooks.IsProAvailable && preferMABoneProxyRootForExternal);
                if (GUILayout.Button("Paste (コピー実行)"))
                    PhysBoneMapperEngine.ApplyCopy(rows, sourcePrefabRoot, targetPrefabRoot,
                        sourceArmatureRoot, targetArmatureRoot,
                        sourceAll, targetAll,
                        copyOtherComponents, cloneExternalPBGameObject,
                        PBMapperHooks.IsProAvailable && preferMABoneProxyRootForExternal,
                        PBMapperHooks.IsProAvailable && enableHighlight);
                GUI.enabled = true;
            }

            // --- ヘッダを統合
            float totalWidth = EditorGUIUtility.currentViewWidth - 30;
            float fixedWidth = 36 + 110 + 120; // 適用 + 種類 + score
            float flexWidth = Mathf.Max(totalWidth - fixedWidth, 300);
            float halfFlex = flexWidth * 0.5f;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("適用", GUILayout.Width(36));
                GUILayout.Label("種類", GUILayout.Width(110));
                GUILayout.Label("コピー元 (Transform)", GUILayout.Width(halfFlex));
                GUILayout.Label("ターゲット", GUILayout.Width(halfFlex));
                GUILayout.Label("score", GUILayout.Width(120));
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var r in rows)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    r.apply = EditorGUILayout.Toggle(r.apply, GUILayout.Width(36));
                    GUILayout.Label(string.IsNullOrEmpty(r.kindLabel) ? r.kind.ToString() : r.kindLabel, GUILayout.Width(110));
                    GUILayout.Label(r.sourceTransform ? r.sourceTransform.GetHierarchyPath() : "(missing)", GUILayout.Width(halfFlex));

                    var rect = GUILayoutUtility.GetRect(halfFlex, EditorGUIUtility.singleLineHeight);
                    Transform current = r.isMAExternal ? r.maRootTarget : r.suggestedTarget;
                    Transform acceptRoot = targetArmatureRoot ? targetArmatureRoot : targetPrefabRoot?.transform;

                    Transform newTarget = (Transform)EditorGUI.ObjectField(rect, current, typeof(Transform), true);
                    if (newTarget != null && !TransformUtilities.IsUnderRoot(acceptRoot, newTarget))
                        newTarget = null;

                    if (r.isMAExternal)
                        r.maRootTarget = newTarget;
                    else
                        r.suggestedTarget = newTarget;

                    // D&D 対応
                    if (rect.Contains(Event.current.mousePosition) && Event.current.type == EventType.DragUpdated)
                    {
                        bool ok = DragAndDrop.objectReferences.All(o => {
                            Transform tf = o is GameObject go ? go.transform : o as Transform;
                            return tf != null && TransformUtilities.IsUnderRoot(acceptRoot, tf);
                        });
                        DragAndDrop.visualMode = ok ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                        Event.current.Use();
                    }
                    if (rect.Contains(Event.current.mousePosition) && Event.current.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (var obj in DragAndDrop.objectReferences)
                        {
                            Transform tf = obj is GameObject go ? go.transform : obj as Transform;
                            if (tf != null && TransformUtilities.IsUnderRoot(acceptRoot, tf))
                            {
                                if (r.isMAExternal)
                                    r.maRootTarget = tf;
                                else
                                    r.suggestedTarget = tf;
                                GUI.changed = true;
                                break;
                            }
                        }
                        Event.current.Use();
                    }

                    GUILayout.Label(r.info, GUILayout.Width(120));
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
#endif
