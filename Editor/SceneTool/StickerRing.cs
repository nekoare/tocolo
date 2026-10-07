using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// シールの回転の輪（面内の回転）。画面上でシールの中心から見たマウスの角度を毎回測り、前回との差を足していく。
    /// 標準の Handles.Disc は、掴んだ点の接線（直線）に沿ってマウスが進んだ距離を「距離 ÷ 半径 × 30°」で角度に換えるので、
    /// 輪が画面で小さいとマウスを動かした以上に回り、輪に沿って半周を過ぎると接線に沿った距離が減って逆回転する（1 回転できない）
    /// </summary>
    internal static class StickerRing
    {
        private static readonly int s_hint = "ClickRecolor.StickerRing".GetHashCode();

        /// <summary>掴んだときの向きと軸の向き（カメラ側か）、掴んでから回した角度（度。画面の角度の差を足したもの）、前回のマウスの画面の角度</summary>
        private static Quaternion s_startRotation;
        private static bool s_axisTowardCamera;
        private static float s_turned;
        private static float s_lastScreenAngle;

        /// <summary>
        /// 輪を描いて操作を受け、回したら rotation を axis（面の向き）のまわりに回す。Handles.matrix（ルートのローカル）の中で、
        /// Handles.zTest と色を決めてから呼ぶ（center・axis・radius もそのローカル）。回したら GUI.changed を立てる
        /// </summary>
        internal static void Do(Vector3 center, Vector3 axis, float radius, ref Quaternion rotation, Color color)
        {
            int id = GUIUtility.GetControlID(s_hint, FocusType.Passive);
            var e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.Layout:
                    // 標準の輪と同じく、輪の線までの画面の距離の半分を登録する（面のつまみなどと重なったときに輪を取りやすく）
                    HandleUtility.AddControl(id, HandleUtility.DistanceToDisc(center, axis, radius) * 0.5f);
                    break;
                case EventType.MouseDown:
                    if (HandleUtility.nearestControl == id && e.button == 0 && !e.alt)
                    {
                        GUIUtility.hotControl = id;
                        s_startRotation = rotation;
                        s_axisTowardCamera = AxisTowardCamera(axis);
                        s_turned = 0f;
                        s_lastScreenAngle = ScreenAngle(center, e.mousePosition);
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        float angle = ScreenAngle(center, e.mousePosition);
                        s_turned = Accumulate(s_turned, s_lastScreenAngle, angle);
                        s_lastScreenAngle = angle;
                        rotation = Turn(s_startRotation, AxisAngle(s_turned, s_axisTowardCamera));
                        GUI.changed = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id && e.button == 0)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    bool active = GUIUtility.hotControl == id || (GUIUtility.hotControl == 0 && HandleUtility.nearestControl == id);
                    using (new Handles.DrawingScope(GUIUtility.hotControl == id ? Handles.selectedColor : active ? Handles.preselectionColor : color))
                    {
                        Handles.DrawWireDisc(center, axis, radius, 2f);
                    }
                    break;
            }
        }

        /// <summary>
        /// 掴んだときの向き start を、その面の向き（start の +Z）のまわりに degrees 回した向き。
        /// 軸は掴んだときの向きから決め、ドラッグ中の今の箱から取り直さない: 取り直すと計算誤差でずれた軸のまわりに「掴んでからの合計の角度」を回すことになり、
        /// 次の軸のずれが前のずれの 2·sin(角度/2) 倍になる（60° を超えると増え続け、120° あたりから面の向きが暴れた）
        /// </summary>
        internal static Quaternion Turn(Quaternion start, float degrees)
        {
            start = Quaternion.Normalize(start);
            return Quaternion.Normalize(Quaternion.AngleAxis(degrees, start * Vector3.forward) * start);
        }

        /// <summary>画面（GUI 座標。y は下向き）で center から mouse への向きの角度（度）。増えると画面で時計回り</summary>
        private static float ScreenAngle(Vector3 center, Vector2 mouse)
        {
            var c = HandleUtility.WorldToGUIPoint(center);
            return Mathf.Atan2(mouse.y - c.y, mouse.x - c.x) * Mathf.Rad2Deg;
        }

        /// <summary>axis（Handles.matrix のローカル）がカメラの側を向いているか</summary>
        private static bool AxisTowardCamera(Vector3 axis)
        {
            var camera = SceneView.currentDrawingSceneView != null ? SceneView.currentDrawingSceneView.camera : Camera.current;
            if (camera == null) return true;
            return Vector3.Dot(Handles.matrix.MultiplyVector(axis), camera.transform.forward) < 0f;
        }

        /// <summary>
        /// 回した角度の合計 turned に、前回の画面の角度 last から今の angle までの差（-180〜180 度の短い向き）を足したもの。
        /// 角度の差で足していくので、±180 度をまたいでも逆回転にならず、何周でも回せる
        /// </summary>
        internal static float Accumulate(float turned, float last, float angle) => turned + Mathf.DeltaAngle(last, angle);

        /// <summary>
        /// 画面で時計回りに screenDegrees 回したときの、軸のまわりの角度。Unity の回転は軸の正の側から見て時計回りが正なので、
        /// 軸がカメラの側を向いていればそのまま、奥を向いていれば逆
        /// </summary>
        internal static float AxisAngle(float screenDegrees, bool axisTowardCamera) => axisTowardCamera ? screenDegrees : -screenDegrees;
    }
}
