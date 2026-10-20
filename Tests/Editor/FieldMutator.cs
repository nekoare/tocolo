using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// RecolorEdit・RecolorSeed のシリアライズされるフィールドを、型ごとに「別の値」へ変える（全フィールドを見るテスト用）。
    /// 作った Unity のオブジェクトは track に渡す（テスト側で破棄する）
    /// </summary>
    internal sealed class FieldMutator
    {
        private readonly Func<Object, Object> _track;

        internal FieldMutator(Func<Object, Object> track)
        {
            _track = track;
        }

        internal Renderer NewRenderer() =>
            ((GameObject)_track(new GameObject("FieldMutator"))).AddComponent<MeshRenderer>();

        internal static IEnumerable<FieldInfo> SerializedFields(Type type)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsNotSerialized) continue;
                yield return field;
            }
        }

        /// <summary>value を別の値にしたもの。知らない型なら null（テストで分類を足してもらう）</summary>
        internal object Mutate(object value, Type type)
        {
            if (type == typeof(bool)) return !(bool)value;
            if (type == typeof(int)) return (int)value + 1;
            if (type == typeof(float)) return (float)value + 0.125f;
            if (type == typeof(string)) return (string)value + "x";
            if (type == typeof(Vector2)) return (Vector2)value + new Vector2(0.25f, 0.5f);
            if (type == typeof(Vector3)) return (Vector3)value + new Vector3(0.25f, 0.5f, 0.75f);
            if (type == typeof(Quaternion)) return (Quaternion)value * Quaternion.Euler(10f, 20f, 30f);
            if (type == typeof(Color)) return (Color)value * 0.5f + new Color(0.1f, 0.2f, 0.3f, 0f);
            if (type.IsEnum)
            {
                var values = Enum.GetValues(type);
                int index = Array.IndexOf(values, value);
                return values.GetValue((index + 1) % values.Length);
            }
            if (type == typeof(Texture2D)) return _track(new Texture2D(2, 2));
            if (type == typeof(Renderer)) return NewRenderer();
            if (type == typeof(List<RecolorSeed>))
            {
                return new List<RecolorSeed>((List<RecolorSeed>)value) { new RecolorSeed { renderer = NewRenderer() } };
            }
            return null;
        }
    }
}
