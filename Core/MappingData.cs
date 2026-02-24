#if UNITY_EDITOR
using System;
using UnityEngine;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace PBMapper
{
    [Serializable]
    public class MappingRow
    {
        public enum Kind { PhysBone, Collider }

        public Kind kind;                       // 論理種別
        public string kindLabel;                // 表示ラベル（通常: PhysBone/Collider, MA時: MA/PhysBone, MA/Collider）
        public Component sourceComponent;
        public Transform sourceTransform;
        public Transform suggestedTarget;
        public bool apply = true;
        public string info;                     // スコアやvia情報
        public bool isMAExternal;               // Armature外でMAが絡む
        public Transform maRootHint;            // MAのRootBone
        public Transform maRootTarget;          // ★ ターゲット側で使うMA Root（UIで編集可能）
        public float score;                     // 推定スコア
    }
}
#endif
