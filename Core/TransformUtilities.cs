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
            if (!parent) return null;
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

        /// <summary>
        /// Armature を推定する。
        /// 1) Humanoid Animator があれば Hips の親
        /// 2) 名前に "armature" を含む Transform のうち最も浅いもの
        ///    （衣装側の "Outfit_Armature" 等より素体の Armature を優先するため）
        /// 3) 見つからなければ root
        /// </summary>
        public static Transform AutoDetectArmature(Transform root)
        {
            if (!root) return null;

            var animator = root.GetComponent<Animator>();
            if (animator && animator.isHuman && animator.avatar)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips && IsUnderRoot(root, hips))
                    return (hips.parent && hips.parent != root) ? hips.parent : hips;
            }

            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (t.name.IndexOf("armature", StringComparison.OrdinalIgnoreCase) >= 0) return t;
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
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
