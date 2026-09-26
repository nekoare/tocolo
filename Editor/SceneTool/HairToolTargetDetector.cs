using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// クリックした Renderer が、同じアバター内のキメラヘアマスター（自作の髪ツール）の対象になっているか。
    /// その髪はアップロード時に髪ツール側の加工が本ツールの結果の上に掛かるので、先に髪ツールで書き出すよう案内する。
    /// 髪ツールのアセンブリは参照せず、型名とフィールド名（targetRenderers）をリフレクションで見る（入っていない環境でも動く）
    /// </summary>
    internal static class HairToolTargetDetector
    {
        private const string ComponentTypeName = "ChimeraHairMaster";
        private const string TargetsFieldName = "targetRenderers";

        internal static bool IsTargetOf(GameObject root, Renderer renderer)
        {
            if (root == null || renderer == null) return false;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().Name != ComponentTypeName) continue;
                var field = component.GetType().GetField(TargetsFieldName);
                if (field == null) continue;
                if (field.GetValue(component) is IEnumerable targets)
                {
                    foreach (var t in targets)
                    {
                        if (t is Renderer r && r == renderer) return true;
                    }
                }
            }
            return false;
        }
    }
}
