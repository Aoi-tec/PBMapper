#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace PBMapper
{
    public static class TransformUtilities
    {
        public static bool IsUnderRoot(Transform root, Transform candidate)
        {
            if (!root || !candidate) return false;
            var t = candidate; while (t) { if (t == root) return true; t = t.parent; }
            return false;
        }

        public static Transform FindChildByName(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c.name == name) return c;
            }
            return null;
        }

        public static void CopyLocalTRS(Transform src, Transform dst)
        {
            if (!src || !dst) return;
            dst.localPosition = src.localPosition;
            dst.localRotation = src.localRotation;
            dst.localScale = src.localScale;
        }

        public static List<Transform> Collect(Transform root)
        {
            var list = new List<Transform>();
            if (!root) return list;
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) list.Add(t);
            return list;
        }

        public static Transform AutoDetectArmature(Transform root)
        {
            if (!root) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.IndexOf("armature", StringComparison.OrdinalIgnoreCase) >= 0) return t;
            }
            return root;
        }
    }

    public static class TransformPathExt
    {
        public static string GetHierarchyPath(this Transform t)
        {
            if (!t) return "";
            var stack = new Stack<string>();
            var cur = t;
            while (cur) { stack.Push(cur.name); cur = cur.parent; }
            return string.Join("/", stack.ToArray());
        }
    }
}
#endif
