using System;
using UnityEngine;

namespace TtsModernBrowser
{
    /// <summary>
    /// Affine map from the screen mesh's local coordinates to its texture UVs, fitted from the
    /// mesh itself. The shader draws the texture with those UVs, so using them for pointer
    /// input keeps clicks aligned with what the player sees, whichever axes the asset uses
    /// and whichever way its UVs run.
    /// </summary>
    internal readonly struct ScreenUv
    {
        private readonly int _axisA;
        private readonly int _axisB;
        private readonly float _uFromA;
        private readonly float _uFromB;
        private readonly float _uOffset;
        private readonly float _vFromA;
        private readonly float _vFromB;
        private readonly float _vOffset;

        internal bool Valid { get; }

        private ScreenUv(bool valid, int axisA, int axisB,
            float uFromA, float uFromB, float uOffset,
            float vFromA, float vFromB, float vOffset)
        {
            Valid = valid;
            _axisA = axisA;
            _axisB = axisB;
            _uFromA = uFromA;
            _uFromB = uFromB;
            _uOffset = uOffset;
            _vFromA = vFromA;
            _vFromB = vFromB;
            _vOffset = vOffset;
        }

        /// <summary>Maps a point in the renderer's local space to texture UV.</summary>
        internal bool TryMap(Vector3 local, out float u, out float v)
        {
            u = 0f;
            v = 0f;
            if (!Valid) return false;

            var a = Component(local, _axisA);
            var b = Component(local, _axisB);

            u = _uFromA * a + _uFromB * b + _uOffset;
            v = _vFromA * a + _vFromB * b + _vOffset;

            const float tolerance = 0.002f;
            return u >= -tolerance && u <= 1f + tolerance &&
                   v >= -tolerance && v <= 1f + tolerance;
        }

        internal static ScreenUv FromMesh(Mesh mesh)
        {
            var invalid = new ScreenUv(false, 0, 1, 0f, 0f, 0f, 0f, 0f, 0f);
            if (mesh == null) return invalid;

            var vertices = mesh.vertices;
            var uvs = mesh.uv;
            if (vertices == null || uvs == null || vertices.Length < 3 || vertices.Length != uvs.Length)
                return invalid;

            // Use the two axes the quad actually spans; a flat mesh has no extent on the third.
            var variance = new float[3];
            for (var axis = 0; axis < 3; axis++)
            {
                var mean = 0f;
                foreach (var vertex in vertices) mean += Component(vertex, axis);
                mean /= vertices.Length;

                var sum = 0f;
                foreach (var vertex in vertices)
                {
                    var delta = Component(vertex, axis) - mean;
                    sum += delta * delta;
                }

                variance[axis] = sum;
            }

            var order = new[] { 0, 1, 2 };
            Array.Sort(order, (left, right) => variance[right].CompareTo(variance[left]));

            var axisA = order[0];
            var axisB = order[1];
            if (variance[axisA] < 1e-8f || variance[axisB] < 1e-8f) return invalid;

            if (!Fit(vertices, uvs, axisA, axisB, true, out var uFromA, out var uFromB, out var uOffset))
                return invalid;
            if (!Fit(vertices, uvs, axisA, axisB, false, out var vFromA, out var vFromB, out var vOffset))
                return invalid;

            return new ScreenUv(true, axisA, axisB, uFromA, uFromB, uOffset, vFromA, vFromB, vOffset);
        }

        /// <summary>Least-squares fit of uv.{u|v} over the two plane axes.</summary>
        private static bool Fit(Vector3[] vertices, Vector2[] uvs, int axisA, int axisB, bool horizontal,
            out float fromA, out float fromB, out float offset)
        {
            fromA = 0f;
            fromB = 0f;
            offset = 0f;

            float saa = 0f, sab = 0f, sbb = 0f, sa = 0f, sb = 0f;
            float ta = 0f, tb = 0f, t = 0f;

            for (var i = 0; i < vertices.Length; i++)
            {
                var a = Component(vertices[i], axisA);
                var b = Component(vertices[i], axisB);
                var value = horizontal ? uvs[i].x : uvs[i].y;

                saa += a * a;
                sab += a * b;
                sbb += b * b;
                sa += a;
                sb += b;
                ta += value * a;
                tb += value * b;
                t += value;
            }

            var n = vertices.Length;
            var det = saa * (sbb * n - sb * sb)
                    - sab * (sab * n - sb * sa)
                    + sa * (sab * sb - sbb * sa);

            if (Mathf.Abs(det) < 1e-9f) return false;

            var detA = ta * (sbb * n - sb * sb)
                     - sab * (tb * n - sb * t)
                     + sa * (tb * sb - sbb * t);
            var detB = saa * (tb * n - sb * t)
                     - ta * (sab * n - sb * sa)
                     + sa * (sab * t - tb * sa);
            var detC = saa * (sbb * t - tb * sb)
                     - sab * (sab * t - tb * sa)
                     + ta * (sab * sb - sbb * sa);

            fromA = detA / det;
            fromB = detB / det;
            offset = detC / det;
            return true;
        }

        private static float Component(Vector3 value, int axis) =>
            axis == 0 ? value.x : axis == 1 ? value.y : value.z;
    }
}
