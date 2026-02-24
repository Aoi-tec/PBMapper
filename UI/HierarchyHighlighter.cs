#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

using VRC.SDK3.Dynamics.PhysBone.Components;

namespace PBMapper
{
    [InitializeOnLoad]
    public static class HierarchyHighlighter
    {
        public static bool Enabled = true;

        private class Info { public bool isPhysBone; public bool edited; }
        static readonly Dictionary<int, Info> map = new();

        static HierarchyHighlighter()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyGUI;
            Undo.postprocessModifications += OnPostprocessMods;

            // フック登録
            PBMapperHooks.ClearAllHighlights = ClearAll;
            PBMapperHooks.Highlight = Highlight;
            PBMapperHooks.SetHighlightEnabled = v => Enabled = v;
            PBMapperHooks.ClearEditedHighlights = ClearEditedOnly;
        }

        private static void OnHierarchyGUI(int instanceID, Rect rect)
        {
            if (!Enabled) return;
            if (!map.TryGetValue(instanceID, out var info)) return;
            var col = info.isPhysBone ? new Color(1f, 0.3f, 0.3f, 0.35f) : new Color(0.3f, 1f, 0.3f, 0.35f);
            EditorGUI.DrawRect(rect, col);
            if (info.edited)
            {
                var r = new Rect(rect.xMax - 8, rect.y, 8, rect.height);
                EditorGUI.DrawRect(r, new Color(1f, 0.9f, 0.2f, 0.9f));
            }
        }

        private static UndoPropertyModification[] OnPostprocessMods(UndoPropertyModification[] mods)
        {
            foreach (var m in mods)
            {
                var target = m.currentValue?.target;
                if (target is Component c)
                {
                    if (c is VRCPhysBone || c is VRCPhysBoneCollider)
                    {
                        int id = c.gameObject.GetInstanceID();
                        if (map.TryGetValue(id, out var info)) info.edited = true;
                    }
                }
            }
            EditorApplication.RepaintHierarchyWindow();
            return mods;
        }

        public static void Highlight(GameObject go, bool isPhysBone)
        {
            if (!go) return; int id = go.GetInstanceID();
            if (!map.TryGetValue(id, out var info)) map[id] = info = new Info { isPhysBone = isPhysBone, edited = false };
            else info.isPhysBone = isPhysBone;
            EditorApplication.RepaintHierarchyWindow();
        }

        public static void ClearEditedOnly()
        {
            var toRemove = map.Where(kv => kv.Value.edited).Select(kv => kv.Key).ToList();
            foreach (var k in toRemove) map.Remove(k);
            EditorApplication.RepaintHierarchyWindow();
        }

        public static void ClearAll()
        { map.Clear(); EditorApplication.RepaintHierarchyWindow(); }
    }

    public class SaveHook : AssetModificationProcessor
    {
        public static string[] OnWillSaveAssets(string[] paths)
        {
            PBMapperHooks.ClearEditedHighlights?.Invoke();
            return paths;
        }
    }
}
#endif
