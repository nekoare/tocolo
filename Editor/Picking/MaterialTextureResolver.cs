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
