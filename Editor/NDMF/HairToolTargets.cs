using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>Renderer が髪ツール（キメラヘアマスター）からどう扱われるか</summary>
    internal enum HairToolRole
    {
        /// <summary>髪ツールの対象でない（髪ツールが無効のときも）</summary>
        None,
        /// <summary>髪ツールの対象・メッシュ統合 OFF で、髪ツールが髪用の入口より前に並ぶ。髪用の入口が髪ツールの出力の上に掛ける</summary>
        Over,
        /// <summary>髪ツールの対象・メッシュ統合 ON で、髪ツールが髪用の入口より前に並ぶ。UV がアトラスに変わるので、どちらの入口も掛けない</summary>
        Merged,
        /// <summary>髪ツールの対象だが、髪ツールが髪用の入口の順序を宣言していない（古い版）。本体が髪ツールより前に掛ける（髪ツールの加工が上に乗る）</summary>
        Legacy,
    }

    /// <summary>プレビュー・焼き込みが担当する Renderer の範囲</summary>
    internal enum RecolorScope
    {
        /// <summary>本体の入口（髪ツールより前）: 役割 None・Legacy</summary>
        Main,
        /// <summary>髪用の入口（髪ツールの後）: 役割 Over</summary>
        ChmHair,
    }

    /// <summary>
    /// 髪ツール（キメラヘアマスター）の対象の判定。髪ツールのアセンブリは参照せず、型名とフィールド名をリフレクションで見る
    /// （入っていない環境でも動く）。
    /// 髪ツールが「髪用の入口より前」を宣言しているか（OrderSupported）は、髪ツールのプラグイン型の定数で見る。
    /// 版番号（asmdef の versionDefines）で見ないのは、宣言そのものの有無を見たいから（版を上げる前の手元の検証でも働く）
    /// </summary>
    internal static class HairToolTargets
    {
        /// <summary>髪用の入口（ClickRecolorChmHairPlugin）の QualifiedName。髪ツールの定数 TocoloHairPluginName と同じ値</summary>
        internal const string HairPluginName = "dev.nekoare.click-recolor.chm-hair";

        private const string ComponentTypeName = "ChimeraHairMaster";
        private const string PluginTypeName = "ChimeraHairMaster.Editor.NDMF.ChimeraHairMasterPlugin";
        private const string OrderFieldName = "TocoloHairPluginName";
        private const string EnabledFieldName = "isEnabled";
        private const string MergeFieldName = "enableMeshMerge";
        private const string TargetsFieldName = "targetRenderers";

        private static bool? s_orderSupported;
        private static IReadOnlyList<Type> s_componentTypes;

        /// <summary>髪ツールが入っていて、髪用の入口より前に並ぶと宣言しているか（ドメインごとに 1 回だけ調べる）</summary>
        internal static bool OrderSupported => s_orderSupported ??= DetectOrderSupported();

        /// <summary>型名が ChimeraHairMaster の Component の型（入っていなければ空）</summary>
        internal static IReadOnlyList<Type> ComponentTypes => s_componentTypes ??= FindComponentTypes();

        private static bool DetectOrderSupported()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try
                {
                    type = assembly.GetType(PluginTypeName, false);
                }
                catch (Exception)
                {
                    continue;
                }
                var field = type?.GetField(OrderFieldName, BindingFlags.Public | BindingFlags.Static);
                if (field == null || !field.IsLiteral) continue;
                if (field.GetRawConstantValue() as string == HairPluginName) return true;
            }
            return false;
        }

        private static IReadOnlyList<Type> FindComponentTypes()
        {
            var result = new List<Type>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<Component>())
            {
                if (type.Name == ComponentTypeName && !type.IsAbstract) result.Add(type);
            }
            return result;
        }

        // ── 髪ツール 1 つの設定 ──

        /// <summary>髪ツールのコンポーネント 1 つの、役割を決める設定（プレビューの観測で値として比べる）</summary>
        internal readonly struct Snapshot : IEquatable<Snapshot>
        {
            public readonly bool enabled;
            public readonly bool merge;
            public readonly Renderer[] targets;

            public Snapshot(bool enabled, bool merge, Renderer[] targets)
            {
                this.enabled = enabled;
                this.merge = merge;
                this.targets = targets ?? Array.Empty<Renderer>();
            }

            public bool Equals(Snapshot other)
            {
                if (enabled != other.enabled || merge != other.merge) return false;
                var a = targets ?? Array.Empty<Renderer>();
                var b = other.targets ?? Array.Empty<Renderer>();
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                {
                    if (!ReferenceEquals(a[i], b[i])) return false;
                }
                return true;
            }

            public override bool Equals(object obj) => obj is Snapshot other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = (enabled ? 1 : 0) * 2 + (merge ? 1 : 0);
                    if (targets != null)
                    {
                        foreach (var t in targets) h = h * 31 + (t != null ? t.GetInstanceID() : 0);
                    }
                    return h;
                }
            }
        }

        /// <summary>
        /// 髪ツールのコンポーネントの設定を読む。統合のフィールドが読めないときは統合 ON とみなす
        /// （髪ツール側の改名で外れたとき、UV が違うかもしれない髪に掛けて崩すより、掛けない方が害が小さい）
        /// </summary>
        internal static Snapshot SnapshotOf(Component component)
        {
            if (component == null) return new Snapshot(false, false, null);
            var type = component.GetType();
            bool enabled = ReadBool(type, component, EnabledFieldName, true);
            bool merge = ReadBool(type, component, MergeFieldName, true);
            var targets = new List<Renderer>();
            if (type.GetField(TargetsFieldName)?.GetValue(component) is IEnumerable list)
            {
                foreach (var t in list)
                {
                    if (t is Renderer r && r != null) targets.Add(r);
                }
            }
            return new Snapshot(enabled, merge, targets.ToArray());
        }

        private static bool ReadBool(Type type, Component component, string name, bool fallback)
        {
            var field = type.GetField(name);
            return field != null && field.FieldType == typeof(bool) ? (bool)field.GetValue(component) : fallback;
        }

        // ── 役割 ──

        /// <summary>
        /// 髪ツールの設定から Renderer ごとの役割を出す（対象でない Renderer は入らない＝None）。
        /// 複数の髪ツールの対象なら Merged を優先する（統合 ON がひとつでもあれば UV が変わりうるので掛けない）
        /// </summary>
        internal static Dictionary<Renderer, HairToolRole> Roles(IEnumerable<Snapshot> snapshots, bool orderSupported)
        {
            var roles = new Dictionary<Renderer, HairToolRole>();
            if (snapshots == null) return roles;
            foreach (var snapshot in snapshots)
            {
                if (!snapshot.enabled || snapshot.targets == null) continue;
                var role = !orderSupported ? HairToolRole.Legacy : snapshot.merge ? HairToolRole.Merged : HairToolRole.Over;
                foreach (var renderer in snapshot.targets)
                {
                    if (renderer == null) continue;
                    if (roles.TryGetValue(renderer, out var current) && current == HairToolRole.Merged) continue;
                    roles[renderer] = role;
                }
            }
            return roles;
        }

        /// <summary>root 配下（非アクティブも含む）の髪ツールから Renderer ごとの役割を出す</summary>
        internal static Dictionary<Renderer, HairToolRole> RolesIn(GameObject root) => RolesIn(root, OrderSupported);

        /// <summary>RolesIn の宣言の有無を渡せる版（テスト用）</summary>
        internal static Dictionary<Renderer, HairToolRole> RolesIn(GameObject root, bool orderSupported)
        {
            var snapshots = new List<Snapshot>();
            if (root != null)
            {
                foreach (var type in ComponentTypes)
                {
                    foreach (var component in root.GetComponentsInChildren(type, true)) snapshots.Add(SnapshotOf(component));
                }
            }
            return Roles(snapshots, orderSupported);
        }

        /// <summary>renderer の属するアバターの髪ツールから見た役割</summary>
        internal static HairToolRole RoleOf(Renderer renderer)
        {
            if (renderer == null) return HairToolRole.None;
            var root = Decal.DecalOverlayMaterial.FindAvatarRoot(renderer.transform);
            return RolesIn(root.gameObject).TryGetValue(renderer, out var role) ? role : HairToolRole.None;
        }

        /// <summary>その役割の Renderer を scope の入口が担当するか</summary>
        internal static bool InScope(RecolorScope scope, HairToolRole role) =>
            scope == RecolorScope.Main
                ? role == HairToolRole.None || role == HairToolRole.Legacy
                : role == HairToolRole.Over;

        /// <summary>
        /// プレビュー用: avatar 配下の髪ツールの設定を観測して（変わるとグループを作り直す）Renderer ごとの役割を出す
        /// </summary>
        internal static Dictionary<Renderer, HairToolRole> ObserveRoles(ComputeContext context, GameObject avatar)
        {
            var snapshots = new List<Snapshot>();
            foreach (var type in ComponentTypes)
            {
                foreach (var component in context.GetComponentsInChildren(avatar, type, true))
                {
                    snapshots.Add(context.Observe(component, SnapshotOf, (a, b) => a.Equals(b)));
                }
            }
            return Roles(snapshots, OrderSupported);
        }

        /// <summary>renderers のうち scope の入口が担当するもの（順序は保つ）</summary>
        internal static List<Renderer> FilterScope(
            IEnumerable<Renderer> renderers, IReadOnlyDictionary<Renderer, HairToolRole> roles, RecolorScope scope)
        {
            var result = new List<Renderer>();
            if (renderers == null) return result;
            foreach (var renderer in renderers)
            {
                if (renderer != null && InScope(scope, RoleIn(roles, renderer))) result.Add(renderer);
            }
            return result;
        }

        /// <summary>roles に無い Renderer は None</summary>
        internal static HairToolRole RoleIn(IReadOnlyDictionary<Renderer, HairToolRole> roles, Renderer renderer) =>
            roles != null && renderer != null && roles.TryGetValue(renderer, out var role) ? role : HairToolRole.None;
    }
}
