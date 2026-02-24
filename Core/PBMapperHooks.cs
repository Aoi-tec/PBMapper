#if UNITY_EDITOR
using System;
using UnityEngine;

namespace PBMapper
{
    /// <summary>
    /// Pro機能のデリゲート置き場。
    /// Pro用ファイルが存在すれば [InitializeOnLoad] で自動登録される。
    /// 存在しなければ null のまま → 呼び出し側は ?. で安全にスキップ。
    /// </summary>
    public static class PBMapperHooks
    {
        // === MA ===
        public static Func<Transform, bool> HasLocalMABoneProxy;
        public static Func<Transform, Transform> TryGetMABoneProxyRoot;
        public static Action<Transform, Transform> UpdateMABoneProxyRootLocal;

        // === Highlight ===
        public static Action ClearAllHighlights;
        public static Action<GameObject, bool> Highlight;
        public static Action<bool> SetHighlightEnabled;
        public static Action ClearEditedHighlights;

        // === 判定 ===
        public static bool IsProAvailable => HasLocalMABoneProxy != null;
    }
}
#endif
