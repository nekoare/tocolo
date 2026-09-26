// 流用元: dev.nekoare.uv-side-splitter/Editor/NDMF/RemoveComponentsPass.cs
using nadena.dev.ndmf;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    public class RemoveComponentsPass : Pass<RemoveComponentsPass>
    {
        public override string DisplayName => "ClickRecolor: Remove Components";

        protected override void Execute(BuildContext context)
        {
            RemoveAll(context.AvatarRootObject);
        }

        /// <summary>BuildContext 無しで呼べる seam。非アクティブな子も含めて全部消し、消した個数を返す</summary>
        internal static int RemoveAll(GameObject root)
        {
            var components = root.GetComponentsInChildren<ClickRecolor>(true);
            foreach (var component in components)
            {
                Object.DestroyImmediate(component);
            }
            return components.Length;
        }
    }
}
