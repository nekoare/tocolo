// 流用元: com.nekoare.mask-creation-tool/Editor/Processing/IslandSelector.cs（RestrictToConnectedComponent）
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// 種画素から 4 連結で到達できる画素だけを残す（CPU）と、マスク RT の CPU 読み書き
    /// </summary>
    internal static class FloodFill
    {
        /// <summary>
        /// mask（0/1、行優先・y=0 が下）の中で seed から 4 連結で到達できる画素だけ残し、他を 0 にする。
        /// 対角で繋がる細い UV ストライプに伝播しないよう 4 連結にする。
        /// seed が範囲外、または seed の画素が 0 なら何もしない（選択を消してしまわないための安全側）
        /// </summary>
        internal static void RestrictToConnected(byte[] mask, int w, int h, Vector2Int seed)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (mask.Length < w * h) throw new ArgumentException("mask の長さが w×h より短いです", nameof(mask));
            if (seed.x < 0 || seed.x >= w || seed.y < 0 || seed.y >= h) return;
            int seedIndex = seed.y * w + seed.x;
            if (mask[seedIndex] == 0) return;

            var visited = new bool[w * h];
            var queue = new Queue<int>();
            queue.Enqueue(seedIndex);
            visited[seedIndex] = true;
            while (queue.Count > 0)
            {
                int index = queue.Dequeue();
                int x = index % w;
                int y = index / w;
                TryVisit(mask, visited, queue, x - 1, y, w, h);
                TryVisit(mask, visited, queue, x + 1, y, w, h);
                TryVisit(mask, visited, queue, x, y - 1, w, h);
                TryVisit(mask, visited, queue, x, y + 1, w, h);
            }

            for (int i = 0; i < w * h; i++)
            {
                if (mask[i] != 0 && !visited[i]) mask[i] = 0;
            }
        }

        private static void TryVisit(byte[] mask, bool[] visited, Queue<int> queue, int x, int y, int w, int h)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int index = y * w + x;
            if (visited[index] || mask[index] == 0) return;
            visited[index] = true;
            queue.Enqueue(index);
        }

        /// <summary>R8 の RT を CPU に読み戻す（0..255、行優先・y=0 が下）</summary>
        internal static byte[] ReadR8(RenderTexture rt)
        {
            if (rt == null) throw new ArgumentNullException(nameof(rt));
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.R8, false, true);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                return tex.GetRawTextureData();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
            }
        }

        /// <summary>data（0..255、行優先・y=0 が下、長さ = rt の画素数）を R8 の RT へ書き戻す</summary>
        internal static void WriteR8(byte[] data, RenderTexture rt)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (rt == null) throw new ArgumentNullException(nameof(rt));
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.R8, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            // Blit は RenderTexture.active を rt に切り替えたまま戻さないので、ReadR8 と同じく元に戻す
            var previous = RenderTexture.active;
            try
            {
                tex.LoadRawTextureData(data);
                tex.Apply(false);
                // どちらも Linear なので色空間の変換は掛からない
                Graphics.Blit(tex, rt);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
            }
        }
    }
}
