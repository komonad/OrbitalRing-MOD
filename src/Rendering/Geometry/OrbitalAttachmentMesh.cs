using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectOrbitalRing.Rendering
{
    internal sealed class OrbitalAttachmentMesh
    {
        private readonly List<Vector3> positions = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector2> coordinates = new List<Vector2>();
        private readonly List<int> triangles = new List<int>();

        internal int TriangleCount => triangles.Count / 3;

        internal void Panel(Vector3 center, Vector3 horizontal, Vector3 vertical, Vector3 normal,
            float width, float height)
        {
            Vector3 halfWidth = horizontal * (width * 0.5f);
            Vector3 halfHeight = vertical * (height * 0.5f);
            Quad(center - halfWidth - halfHeight, center + halfWidth - halfHeight,
                center + halfWidth + halfHeight, center - halfWidth + halfHeight, normal);
            int start = coordinates.Count - 4;
            coordinates[start] = Vector2.zero;
            coordinates[start + 1] = Vector2.right;
            coordinates[start + 2] = Vector2.one;
            coordinates[start + 3] = Vector2.up;
        }

        internal void Box(Vector3 center, Vector3 side, Vector3 up, Vector3 forward, Vector3 size)
        {
            Vector3[] axes =
            {
                side * size.x * 0.5f,
                up * size.y * 0.5f,
                forward * size.z * 0.5f
            };
            for (int face = 0; face < 3; face++)
            {
                Vector3 first = axes[(face + 1) % 3];
                Vector3 second = axes[(face + 2) % 3];
                foreach (float sign in new[] { -1f, 1f })
                {
                    Vector3 normal = axes[face].normalized * sign;
                    Vector3 offset = center + axes[face] * sign;
                    Quad(offset - first - second, offset + first - second,
                        offset + first + second, offset - first + second, normal);
                }
            }
        }

        internal void BeveledBox(Vector3 center, Vector3 side, Vector3 up, Vector3 forward, Vector3 size, float bevel)
        {
            float halfWidth = size.x * 0.5f;
            float halfHeight = size.y * 0.5f;
            bevel = Mathf.Min(bevel, Mathf.Min(halfWidth, halfHeight));
            Vector2[] section =
            {
                new Vector2(-halfWidth + bevel, -halfHeight),
                new Vector2(halfWidth - bevel, -halfHeight),
                new Vector2(halfWidth, -halfHeight + bevel),
                new Vector2(halfWidth, halfHeight - bevel),
                new Vector2(halfWidth - bevel, halfHeight),
                new Vector2(-halfWidth + bevel, halfHeight),
                new Vector2(-halfWidth, halfHeight - bevel),
                new Vector2(-halfWidth, -halfHeight + bevel),
            };
            Vector3 depth = forward * size.z * 0.5f;
            for (int index = 0; index < section.Length; index++)
            {
                Vector2 first = section[index];
                Vector2 next = section[(index + 1) % section.Length];
                Vector3 firstPoint = center + side * first.x + up * first.y;
                Vector3 nextPoint = center + side * next.x + up * next.y;
                Vector2 edge = next - first;
                Vector3 normal = (side * edge.y - up * edge.x).normalized;
                Quad(firstPoint - depth, nextPoint - depth, nextPoint + depth, firstPoint + depth, normal);
                Triangle(center - depth, firstPoint - depth, nextPoint - depth, -forward);
                Triangle(center + depth, firstPoint + depth, nextPoint + depth, forward);
            }
        }

        internal void Beam(Vector3 first, Vector3 last, Vector3 up, float width, float height)
        {
            Vector3 forward = (last - first).normalized;
            Vector3 side = Vector3.Cross(up, forward).normalized;
            Vector3 normal = Vector3.Cross(forward, side).normalized;
            Box((first + last) * 0.5f, side, normal, forward,
                new Vector3(width, height, Vector3.Distance(first, last)));
        }

        internal void Cylinder(Vector3 first, Vector3 last, float radius)
        {
            Vector3 axis = (last - first).normalized;
            Vector3 up = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Tube((first + last) * 0.5f, axis, up, 0, radius, Vector3.Distance(first, last), 12);
        }

        internal void BeveledBeam(Vector3 first, Vector3 last, Vector3 normal, float width, float height, float bevel)
        {
            Vector3 forward = (last - first).normalized;
            Vector3 side = Vector3.Cross(normal, forward).normalized;
            Vector3 up = Vector3.Cross(forward, side).normalized;
            BeveledBox((first + last) * 0.5f, side, up, forward,
                new Vector3(width, height, Vector3.Distance(first, last)), bevel);
        }

        internal void Tube(Vector3 center, Vector3 axis, Vector3 up, float innerRadius, float outerRadius,
            float length, int segments = 32, bool continuousUv = false)
        {
            Vector3 side = Vector3.Cross(up, axis).normalized;
            up = Vector3.Cross(axis, side).normalized;
            Vector3 depth = axis * length * 0.5f;
            for (int index = 0; index < segments; index++)
            {
                float firstAngle = index * Mathf.PI * 2 / segments;
                float nextAngle = (index + 1) * Mathf.PI * 2 / segments;
                Vector3 first = side * Mathf.Cos(firstAngle) + up * Mathf.Sin(firstAngle);
                Vector3 next = side * Mathf.Cos(nextAngle) + up * Mathf.Sin(nextAngle);
                Quad(center + first * outerRadius - depth, center + next * outerRadius - depth,
                    center + next * outerRadius + depth, center + first * outerRadius + depth,
                    first, next, next, first);
                if (continuousUv)
                {
                    int start = coordinates.Count - 4;
                    coordinates[start] = new Vector2((float)index / segments, 0);
                    coordinates[start + 1] = new Vector2((float)(index + 1) / segments, 0);
                    coordinates[start + 2] = new Vector2((float)(index + 1) / segments, 1);
                    coordinates[start + 3] = new Vector2((float)index / segments, 1);
                }

                if (innerRadius > 0)
                {
                    Quad(center + first * innerRadius - depth, center + next * innerRadius - depth,
                        center + next * innerRadius + depth, center + first * innerRadius + depth,
                        -first, -next, -next, -first);
                }

                foreach (float sign in new[] { -1f, 1f })
                {
                    Vector3 cap = center + depth * sign;
                    if (innerRadius > 0)
                    {
                        Quad(cap + first * innerRadius, cap + next * innerRadius,
                            cap + next * outerRadius, cap + first * outerRadius, axis * sign);
                    }
                    else
                    {
                        Triangle(cap, cap + first * outerRadius, cap + next * outerRadius, axis * sign);
                    }
                }
            }
        }

        internal void Sweep(IReadOnlyList<Vector3> path, Vector3 referenceUp, float radius)
        {
            const int segments = 16;
            if (path.Count < 2)
            {
                return;
            }

            var tangents = new Vector3[path.Count];
            var sides = new Vector3[path.Count];
            var across = new Vector3[path.Count];
            for (int index = 0; index < path.Count; index++)
            {
                tangents[index] = (path[Mathf.Min(index + 1, path.Count - 1)]
                    - path[Mathf.Max(0, index - 1)]).normalized;
                Vector3 side = index == 0 ? Vector3.Cross(referenceUp, tangents[index]) :
                    Vector3.ProjectOnPlane(sides[index - 1], tangents[index]);
                if (side.sqrMagnitude < 0.000001f)
                {
                    side = Vector3.Cross(Mathf.Abs(tangents[index].y) < 0.9f ? Vector3.up : Vector3.right,
                        tangents[index]);
                }

                sides[index] = side.normalized;
                across[index] = Vector3.Cross(tangents[index], sides[index]).normalized;
            }

            for (int index = 0; index < path.Count - 1; index++)
            {
                for (int column = 0; column < segments; column++)
                {
                    float angle = column * Mathf.PI * 2 / segments;
                    float nextAngle = (column + 1) * Mathf.PI * 2 / segments;
                    Vector3 first = sides[index] * Mathf.Cos(angle) + across[index] * Mathf.Sin(angle);
                    Vector3 second = sides[index] * Mathf.Cos(nextAngle) + across[index] * Mathf.Sin(nextAngle);
                    Vector3 third = sides[index + 1] * Mathf.Cos(nextAngle) + across[index + 1] * Mathf.Sin(nextAngle);
                    Vector3 fourth = sides[index + 1] * Mathf.Cos(angle) + across[index + 1] * Mathf.Sin(angle);
                    Quad(path[index] + first * radius, path[index] + second * radius,
                        path[index + 1] + third * radius, path[index + 1] + fourth * radius,
                        first, second, third, fourth);
                }
            }

            foreach (int index in new[] { 0, path.Count - 1 })
            {
                Vector3 normal = tangents[index] * (index == 0 ? -1 : 1);
                for (int column = 0; column < segments; column++)
                {
                    float angle = column * Mathf.PI * 2 / segments;
                    float nextAngle = (column + 1) * Mathf.PI * 2 / segments;
                    Vector3 first = sides[index] * Mathf.Cos(angle) + across[index] * Mathf.Sin(angle);
                    Vector3 last = sides[index] * Mathf.Cos(nextAngle) + across[index] * Mathf.Sin(nextAngle);
                    Triangle(path[index], path[index] + first * radius, path[index] + last * radius, normal);
                }
            }
        }

        internal void ArcBand(Vector3 center, Vector3 axis, Vector3 forward, float innerRadius, float outerRadius,
            float height, float halfAngle, int segments = 16)
        {
            Vector3 side = Vector3.Cross(axis, forward).normalized;
            forward = Vector3.Cross(side, axis).normalized;
            Vector3 lower = center - axis * (height * 0.5f);
            Vector3 upper = center + axis * (height * 0.5f);
            for (int index = 0; index < segments; index++)
            {
                float firstAngle = Mathf.Lerp(-halfAngle, halfAngle, (float)index / segments);
                float lastAngle = Mathf.Lerp(-halfAngle, halfAngle, (float)(index + 1) / segments);
                Vector3 first = forward * Mathf.Cos(firstAngle) + side * Mathf.Sin(firstAngle);
                Vector3 last = forward * Mathf.Cos(lastAngle) + side * Mathf.Sin(lastAngle);
                Quad(lower + first * outerRadius, lower + last * outerRadius,
                    upper + last * outerRadius, upper + first * outerRadius, first, last, last, first);
                Quad(lower + first * innerRadius, lower + last * innerRadius,
                    upper + last * innerRadius, upper + first * innerRadius, -first, -last, -last, -first);
                Quad(upper + first * innerRadius, upper + last * innerRadius,
                    upper + last * outerRadius, upper + first * outerRadius, axis);
                Quad(lower + first * innerRadius, lower + last * innerRadius,
                    lower + last * outerRadius, lower + first * outerRadius, -axis);
                if (index == 0)
                {
                    Vector3 normal = forward * Mathf.Sin(firstAngle) - side * Mathf.Cos(firstAngle);
                    Quad(lower + first * innerRadius, lower + first * outerRadius,
                        upper + first * outerRadius, upper + first * innerRadius, normal);
                }

                if (index == segments - 1)
                {
                    Vector3 normal = -forward * Mathf.Sin(lastAngle) + side * Mathf.Cos(lastAngle);
                    Quad(lower + last * innerRadius, lower + last * outerRadius,
                        upper + last * outerRadius, upper + last * innerRadius, normal);
                }
            }
        }

        private void Triangle(Vector3 first, Vector3 second, Vector3 third, Vector3 normal)
        {
            int start = positions.Count;
            positions.Add(first);
            positions.Add(second);
            positions.Add(third);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            coordinates.Add(Vector2.zero);
            coordinates.Add(Vector2.right);
            coordinates.Add(Vector2.up);
            Face(start, start + 1, start + 2, Vector3.Dot(Vector3.Cross(second - first, third - first), normal) < 0);
        }

        private void Quad(Vector3 first, Vector3 second, Vector3 third, Vector3 fourth, Vector3 normal) =>
            Quad(first, second, third, fourth, normal, normal, normal, normal);

        private void Quad(Vector3 first, Vector3 second, Vector3 third, Vector3 fourth,
            Vector3 firstNormal, Vector3 secondNormal, Vector3 thirdNormal, Vector3 fourthNormal)
        {
            int start = positions.Count;
            positions.Add(first);
            positions.Add(second);
            positions.Add(third);
            positions.Add(fourth);
            normals.Add(firstNormal);
            normals.Add(secondNormal);
            normals.Add(thirdNormal);
            normals.Add(fourthNormal);
            float width = Vector3.Distance(first, second) / 4;
            float height = Vector3.Distance(second, third) / 4;
            coordinates.Add(Vector2.zero);
            coordinates.Add(new Vector2(width, 0));
            coordinates.Add(new Vector2(width, height));
            coordinates.Add(new Vector2(0, height));
            bool reverse = Vector3.Dot(Vector3.Cross(second - first, third - first),
                firstNormal + secondNormal + thirdNormal) < 0;
            Face(start, start + 1, start + 2, reverse);
            Face(start, start + 2, start + 3, reverse);
        }

        private void Face(int first, int second, int third, bool reverse)
        {
            triangles.Add(first);
            triangles.Add(reverse ? third : second);
            triangles.Add(reverse ? second : third);
        }

        internal Mesh Build(bool tangentSpace = false)
        {
            var mesh = new Mesh
            {
                name = "OrbitalRing.Attachment",
                indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, coordinates);
            mesh.SetTriangles(triangles, 0);
            if (tangentSpace)
            {
                mesh.RecalculateTangents();
            }

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
