#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace PBMapper
{
    [InitializeOnLoad]
    public static class ModularAvatarHandler
    {
        static ModularAvatarHandler()
        {
            PBMapperHooks.HasLocalMABoneProxy = HasLocalMABoneProxy;
            PBMapperHooks.TryGetMABoneProxyRoot = TryGetMABoneProxyRoot;
            PBMapperHooks.UpdateMABoneProxyRootLocal = UpdateMABoneProxyRootLocal;
        }

        public static bool IsMABoneProxy(Component c)
        {
            if (!c) return false;
            var t = c.GetType();
            var n = t.FullName ?? t.Name;
            if (n == "nadena.dev.modular_avatar.core.ModularAvatarBoneProxy") return true;
            return n.Contains("ModularAvatarBoneProxy") || n.Contains("MABoneProxy") || n.EndsWith(".BoneProxy");
        }

        public static Transform TryGetMABoneProxyRoot(Transform holder)
        {
            if (!holder) return null;

            IEnumerable<Transform> Enumerate()
            {
                yield return holder;
                for (var p = holder.parent; p; p = p.parent) yield return p;
                foreach (var c in holder.GetComponentsInChildren<Transform>(true)) yield return c;
            }

            foreach (var tf in Enumerate())
            {
                foreach (var comp in tf.GetComponents<Component>())
                {
                    if (!IsMABoneProxy(comp)) continue;

                    var t = comp.GetType();
                    var prop = t.GetProperty("target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (prop != null && typeof(Transform).IsAssignableFrom(prop.PropertyType))
                    {
                        var v = prop.GetValue(comp) as Transform;
                        if (v) return v;
                    }

                    var so = new SerializedObject(comp);
                    var it = so.GetIterator();
                    bool enter = true;
                    while (it.NextVisible(enter))
                    {
                        enter = false;
                        if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (it.objectReferenceValue is Transform tr)
                        {
                            var name = it.name.ToLowerInvariant();
                            if (name.Contains("target") || name.Contains("root") || name.Contains("bone"))
                                return tr;
                        }
                    }
                }
            }
            return null;
        }

        public static bool HasLocalMABoneProxy(Transform holder)
        {
            if (!holder) return false;
            foreach (var c in holder.GetComponents<Component>())
            {
                if (!c) continue;
                var n = (c.GetType().FullName ?? c.GetType().Name);
                if (n.Contains("MABoneProxy") || n.Contains("ModularAvatarBoneProxy") || n.Contains("BoneProxy"))
                    return true;
            }
            return false;
        }

        public static void UpdateMABoneProxyRootLocal(Transform go, Transform newRoot)
        {
            if (!go || !newRoot) return;

            foreach (var comp in go.GetComponents<Component>())
            {
                if (!IsMABoneProxy(comp)) continue;

                Undo.RecordObject(comp, "Set MA BoneProxy Target");

                var tp = comp.GetType();
                var prop = tp.GetProperty("target", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);

                if (prop != null && prop.CanWrite && typeof(Transform).IsAssignableFrom(prop.PropertyType))
                {
                    prop.SetValue(comp, newRoot);
                    EditorUtility.SetDirty(comp);
                    continue;
                }

                var so = new SerializedObject(comp);
                bool changed = false;

                foreach (var key in new[] { "target", "rootBone", "rootTransform", "bone", "_root", "m_Root", "m_RootBone" })
                {
                    var sp = so.FindProperty(key);
                    if (sp != null && sp.propertyType == SerializedPropertyType.ObjectReference)
                    { sp.objectReferenceValue = newRoot; changed = true; break; }
                }

                if (!changed)
                {
                    var it = so.GetIterator();
                    bool enter = true;
                    while (it.NextVisible(enter))
                    {
                        enter = false;
                        if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var name = it.name.ToLowerInvariant();
                        if (name.Contains("target") || name.Contains("root") || name.Contains("bone"))
                        { it.objectReferenceValue = newRoot; changed = true; break; }
                    }
                }

                if (changed) { so.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(comp); }
            }
        }
    }
}
#endif
