using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Picking
{
    public struct MainTextureInfo
    {
        public string propertyName;
        public Texture2D texture;
        public Vector2 scale;
        public Vector2 offset;
    }

    /// <summary>マテリアルから「メインテクスチャ」を探す。lilToon は _MainTex、URP 系は _BaseMap</summary>
    public static class MaterialTextureResolver
    {
        public static readonly string[] TexturePropertyNames = { "_MainTex", "_BaseMap", "_BaseColorMap" };

        public static bool TryGetMainTexture(Material material, out MainTextureInfo info)
        {
            info = default;
            if (material == null) return false;
            foreach (var name in TexturePropertyNames)
            {
                if (!material.HasProperty(name)) continue;
                var tex = material.GetTexture(name) as Texture2D;
                if (tex == null) continue;
                info = new MainTextureInfo
                {
                    propertyName = name,
                    texture = tex,
                    scale = material.GetTextureScale(name),
                    offset = material.GetTextureOffset(name),
                };
                return true;
            }
            return false;
        }

        /// <summary>
        /// root 配下の全 Renderer（無効な物も含む。アニメーションで出す衣装など）のマテリアルが使っているメインテクスチャ。
        /// 編集のテクスチャがアバター内で使われているかの判定に使う（ユーザー要望 2026-10-03）
        /// </summary>
        public static HashSet<Texture2D> CollectMainTextures(GameObject root)
        {
            var result = new HashSet<Texture2D>();
            if (root == null) return result;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (TryGetMainTexture(material, out var info)) result.Add(info.texture);
                }
            }
            return result;
        }

        /// <summary>
        /// マテリアル枠 slot が描くサブメッシュ。枠がサブメッシュより多いと、Unity は余った枠で最後のサブメッシュを重ね描きする
        /// （lilToon の FakeShadow をよくこう付ける。枠が [_FakeShadow, 髪] の順だと髪は余った枠になる）。subMeshCount が 0 なら -1
        /// </summary>
        public static int SubmeshOfSlot(int slot, int subMeshCount) => subMeshCount <= 0 ? -1 : Mathf.Min(slot, subMeshCount - 1);

        /// <summary>UV0 に Tiling/Offset を掛けて [0,1) に畳む</summary>
        public static Vector2 ToTextureCoord(in MainTextureInfo info, Vector2 uv)
        {
            Vector2 scale = info.scale == Vector2.zero ? Vector2.one : info.scale;
            Vector2 t = Vector2.Scale(uv, scale) + info.offset;
            return new Vector2(t.x - Mathf.Floor(t.x), t.y - Mathf.Floor(t.y));
        }

        /// <summary>テクスチャ座標 → テクセル（クランプ）。UV もテクセルも Y は下が 0</summary>
        public static Vector2Int ToTexel(in MainTextureInfo info, Vector2 coord)
        {
            int w = info.texture != null ? info.texture.width : 1;
            int h = info.texture != null ? info.texture.height : 1;
            return new Vector2Int(
                Mathf.Clamp(Mathf.FloorToInt(coord.x * w), 0, w - 1),
                Mathf.Clamp(Mathf.FloorToInt(coord.y * h), 0, h - 1));
        }
    }
}
