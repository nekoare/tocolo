using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// Scene で画像をつかんで表面に沿って滑らせる 1 回分（押し・ドラッグ・離しは RecolorSceneTool が渡す）。
    /// 箱の掴んだ点（箱のローカル）がカーソルの下の面に来るように箱を動かし、向きはその面に合わせる（画像の面内の傾きは保つ・大きさは変えない。
    /// パネルの「向きも面に合わせる」が OFF なら向きはそのまま）。置き方がシールなら、箱の中心は掴んだ点の奥行きのずれを持ち越さず面の上に乗せ直す
    /// （StickerSurface。持ち越すと曲面で中心が面から浮き沈みし、枠がずれたり縮めたときに面が奥行きから外れたりする）。
    /// 箱のギズモの矢印は、対象ルートの向きでも箱の向きでも、傾いた面に沿った方向と合わず動かしにくいので、面の上を直接たどる。
    /// カーソルの下がメンバーのテクスチャの面なら選択範囲の外でも動かし、画像が選択範囲に 1 点もかからなくなる手前で止める（見切れるぎりぎりまで。
    /// カーソルの下が範囲の中かで止めると、画像の中心あたりを掴んでいるので画像の半分が外に出たところで止まっていた）
    /// </summary>
    internal sealed class DecalSurfaceDrag
    {
        /// <summary>ドラッグしている編集の id</summary>
        public string EditId { get; }
        /// <summary>掴む前の箱（Esc で戻す）</summary>
        public Vector3 StartPosition { get; }
        public Quaternion StartRotation { get; }
        /// <summary>レイを当てる Renderer（メンバーのテクスチャを使う物だけ。手前の髪などは素通りして範囲の面に当てる）</summary>
        public List<Renderer> Renderers { get; } = new List<Renderer>();

        /// <summary>掴んだ点（箱のローカル）</summary>
        private readonly Vector3 _grabLocal;
        /// <summary>掴んだときの画像の面内の傾き（DecalBox.TwistOf）</summary>
        private readonly float _twist;
        /// <summary>面の向きを平均する広さ（画像が覆う広さ。DecalBox.ImageFootprintRadius）</summary>
        private readonly float _radius;
        /// <summary>Renderer → その三角形（対象ルートのローカル。その Renderer に初めて当たったときに 1 回だけ集める。ドラッグ中はポーズが変わらない）</summary>
        private readonly Dictionary<Renderer, List<SurfaceTriangle>> _triangles = new Dictionary<Renderer, List<SurfaceTriangle>>();
        /// <summary>テクスチャ → そのテクスチャの編集（連結のメンバー）</summary>
        private readonly Dictionary<Texture2D, RecolorEdit> _members = new Dictionary<Texture2D, RecolorEdit>();
        /// <summary>直前に置いた向き（面に合わせないときはこれを使う）</summary>
        private Quaternion _rotation;
        /// <summary>
        /// 箱の向きをカーソルの下の面に合わせるか。シールはいつも合わせ（面とずれた向きで展開すると歪むだけ）、箱は掴んだときの
        /// ToolSession.DecalDragFollowsSurface（ドラッグの途中では変えない）
        /// </summary>
        private readonly bool _followSurface;
        /// <summary>編集の置き方がシールか（RecolorEdit.decalSticker）。シールなら中心を面の上に乗せ直す</summary>
        private readonly bool _sticker;
        /// <summary>箱の奥行き（中心を乗せ直す面を探す長さ）</summary>
        private readonly float _depth;
        /// <summary>選択範囲の表面の点（対象ルートのローカル。掴んだときに 1 回だけ集める。画像が範囲にかかっているかの判定）</summary>
        private readonly List<Vector3> _selectionPoints = new List<Vector3>();
        /// <summary>見えている画像の縦横の半分（箱のローカルの x・y）</summary>
        private readonly Vector2 _imageHalf;
        /// <summary>画像が範囲にかかっているかを見る奥行きの半分（シールは回り込むので、面に沿って届く距離まで）</summary>
        private readonly float _overlapDepth;

        private DecalSurfaceDrag(RecolorEdit edit, Vector3 grabLocal)
        {
            EditId = edit.id;
            StartPosition = edit.decalBoxPosition;
            StartRotation = Quaternion.Normalize(edit.decalBoxRotation);
            _rotation = StartRotation;
            _followSurface = edit.decalSticker || ToolSession.DecalDragFollowsSurface;
            _sticker = edit.decalSticker;
            _depth = Mathf.Abs(edit.decalBoxSize.z);
            // シールは中心を面の上に乗せ直すので、掴んだ点の奥行きのずれは使わない（面に沿ったずれだけ保つ）
            _grabLocal = _sticker ? new Vector3(grabLocal.x, grabLocal.y, 0f) : grabLocal;
            _twist = DecalBox.TwistOf(StartRotation);
            var fit = DecalLayerBuilder.FitScale(edit.decalTexture, edit.decalBoxSize, edit.decalKeepAspect);
            _radius = DecalBox.ImageFootprintRadius(edit.decalBoxSize, fit);
            _imageHalf = new Vector2(Mathf.Abs(edit.decalBoxSize.x) / Mathf.Max(fit.x, 1e-6f), Mathf.Abs(edit.decalBoxSize.y) / Mathf.Max(fit.y, 1e-6f)) * 0.5f;
            _overlapDepth = _sticker
                ? Mathf.Max(_depth * 0.5f, Decal.DecalMappings.ImageHalfDiagonal(edit.decalBoxSize, fit) * Decal.DecalMappings.ReachMargin)
                : _depth * 0.5f;
        }

        /// <summary>
        /// 画像の grabWorld（ワールド）を掴んで始める。pickRenderers はクリックで当てる Renderer（除外リスト外・表示中）
        /// </summary>
        internal static DecalSurfaceDrag Begin(ClickRecolor component, RecolorEdit edit, Vector3 grabWorld, IReadOnlyList<Renderer> pickRenderers)
        {
            var rotation = Quaternion.Normalize(edit.decalBoxRotation);
            var grabRoot = component.transform.worldToLocalMatrix.MultiplyPoint3x4(grabWorld);
            var drag = new DecalSurfaceDrag(edit, Quaternion.Inverse(rotation) * (grabRoot - edit.decalBoxPosition));
            foreach (var member in EditGroups.Members(component, edit))
            {
                if (member.sourceTexture != null) drag._members[member.sourceTexture] = member;
            }
            foreach (var renderer in pickRenderers)
            {
                if (renderer != null && StickerSurface.UsesAnyTexture(renderer, drag._members)) drag.Renderers.Add(renderer);
            }
            GradientBox.DefaultFor(component, edit, drag._selectionPoints);
            return drag;
        }

        /// <summary>
        /// カーソルの下の面 hit へ動かした edit の箱の位置・向き（対象ルートのローカル）。hit がメンバーのテクスチャの面でない、または
        /// 動かした先で画像が選択範囲に 1 点もかからない（Overlaps）なら false（箱は動かさない）。選択範囲の点が集められなかったときは、
        /// 今までどおり hit が範囲の中のときだけ動かす
        /// </summary>
        internal bool TryMove(ClickRecolor component, RecolorEdit edit, in PickHit hit, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = _rotation;
            if (!hit.hasMainTexture || hit.mainTexture.texture == null) return false;
            var texture = hit.mainTexture.texture;
            if (!_members.TryGetValue(texture, out var member)) return false;
            if (_selectionPoints.Count == 0 && !StickerSurface.IsSelected(component, edit, member, texture, hit.uv)) return false;

            var toRoot = component.transform.worldToLocalMatrix;
            if (!_triangles.TryGetValue(hit.renderer, out var triangles))
            {
                triangles = SurfaceTriangles.Collect(hit.renderer, toRoot, _members.Keys);
                _triangles[hit.renderer] = triangles;
            }
            Slide(triangles, _radius, toRoot.MultiplyPoint3x4(hit.worldPosition), toRoot.MultiplyVector(hit.worldNormal),
                _twist, _grabLocal, _rotation, _followSurface, out position, out rotation);
            if (_sticker)
            {
                // 掴んだ点から面に沿ってずらした中心の真上（箱の正面）から奥へ、選択範囲の一番手前の面に乗せる。範囲の外へ出ていれば、
                // メンバーのテクスチャの一番手前の面に乗せる（展開の種は面の上に要る）。どちらも見つからなければ面の接平面の上のまま
                var normal = rotation * Vector3.forward;
                var origin = position + normal * (_depth * 0.5f);
                if (StickerSurface.TryFindFrontSurface(component, edit, origin, -normal, _depth, out var onSurface, Renderers)
                    || StickerSurface.TryFindFrontSurface(component, edit, origin, -normal, _depth, out onSurface, Renderers, selectedOnly: false))
                {
                    position = onSurface;
                }
            }
            if (_selectionPoints.Count > 0 && !Overlaps(_selectionPoints, position, rotation, _imageHalf, _overlapDepth)) return false;
            _rotation = rotation;
            return true;
        }

        /// <summary>
        /// 箱（中心 position・向き rotation）の画像の長方形（縦横の半分 imageHalf、奥行きの半分 depth）に、選択範囲の点 points が 1 つでも入るか。
        /// 1 つも入らなければ画像は選択範囲の外にあって見えない。シールの回り込んだ所は長方形の中へ折り返して写るので、奥行きを届く距離まで取れば数えられる
        /// </summary>
        internal static bool Overlaps(IReadOnlyList<Vector3> points, Vector3 position, Quaternion rotation, Vector2 imageHalf, float depth)
        {
            var toBox = Quaternion.Inverse(Quaternion.Normalize(rotation));
            foreach (var p in points)
            {
                var local = toBox * (p - position);
                if (Mathf.Abs(local.x) <= imageHalf.x && Mathf.Abs(local.y) <= imageHalf.y && Mathf.Abs(local.z) <= depth) return true;
            }
            return false;
        }

        /// <summary>
        /// 面の上の点 anchor（表の向き anchorNormal。どちらも対象ルートのローカル）へ、箱の掴んだ点 grabLocal を合わせた箱の位置と向き。
        /// followSurface なら、向きは画像の中心のまわり radius 以内の三角形の向きの平均（DecalBox.SurfaceNormal）に、画像の面内の傾き twist を保って合わせる。
        /// 画像の中心は向きで決まるので、まず掴んだ点のまわりの向きで中心を仮に置き、その中心のまわりで向きを測り直す
        /// （掴んだ点のまわりの向きのままだと、画像の端を掴んだとき中心ではなく端の面の向きになる）。面に合わせないときは previous のまま（位置だけ動かす）
        /// </summary>
        internal static void Slide(IReadOnlyList<SurfaceTriangle> triangles, float radius, Vector3 anchor, Vector3 anchorNormal, float twist,
            Vector3 grabLocal, Quaternion previous, bool followSurface, out Vector3 position, out Quaternion rotation)
        {
            rotation = previous;
            if (followSurface)
            {
                var grabbed = DecalBox.SurfaceNormal(triangles, anchor, anchorNormal, radius);
                var center = anchor - DecalBox.FacingWithTwist(grabbed, twist) * grabLocal;
                rotation = DecalBox.FacingWithTwist(DecalBox.SurfaceNormal(triangles, center, grabbed, radius), twist);
            }
            position = anchor - rotation * grabLocal;
        }
    }
}
