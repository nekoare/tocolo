using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests.Fakes
{
    /// <summary>
    /// キメラヘアマスターのコンポーネントの代役。本体は型名とフィールド名（リフレクション）だけで見るので、
    /// 本物のパッケージを入れずに役割の判定・髪用の入口を試せる。型名・フィールド名・型は本物に合わせること。
    /// テスト本体（Editor 専用アセンブリ）に置かず別のアセンブリにしている: Editor 専用アセンブリの MonoBehaviour は
    /// GameObject に付けられず、AddComponent が null を返す。メニューに出さないのは、テストを含めてコンパイルされた環境で
    /// 本物と同じ名前のコンポーネントが「Add Component」に並ばないようにするため
    /// </summary>
    [AddComponentMenu("")]
    public class ChimeraHairMaster : MonoBehaviour
    {
        public bool isEnabled = true;
        public bool enableMeshMerge;
        public List<SkinnedMeshRenderer> targetRenderers = new List<SkinnedMeshRenderer>();
    }
}
