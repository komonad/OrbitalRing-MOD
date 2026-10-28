using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectOrbitalRing.Rendering
{
    internal sealed class TrackMesh
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector2> coordinates = new List<Vector2>();
        private readonly List<int>[] indices =
        {
            new List<int>(),
            new List<int>(),
            new List<int>(),
            new List<int>(),
            new List<int>(),
        };
        private readonly float radius;
        private readonly Func<Vector3, float> textureCoordinate;
        private readonly Vector3 textureOrigin;
        private readonly TrackGeometryProfile geometry;
        private readonly float textureCoordinatePeriod;
        private readonly int detailLevel;
        internal int VertexCount => vertices.Count;

        private readonly struct TubeRow
        {
            internal readonly Vector3 Position;
            internal readonly Vector3 Tangent;
            internal readonly Vector3 Side;
            internal readonly Vector3 Radial;
            internal readonly float Coordinate;

            internal TubeRow(Vector3 position, Vector3 tangent, Vector3 up, float coordinate)
            {
                Position = position;
                Tangent = tangent.normalized;
                Side = Vector3.Cross(up, Tangent).normalized;
                Radial = Vector3.Cross(Tangent, Side).normalized;
                Coordinate = coordinate;
            }
        }

        internal TrackMesh(float radius = 0.32f, Func<Vector3, float> textureCoordinate = null,
            Vector3 textureOrigin = default(Vector3), TrackGeometryProfile geometry = null,
            float textureCoordinatePeriod = 0, int detailLevel = 0)
        {
            this.radius = radius;
            this.textureCoordinate = textureCoordinate;
            this.textureOrigin = textureOrigin;
            this.geometry = geometry ?? TrackGeometryProfile.Default;
            this.textureCoordinatePeriod = textureCoordinatePeriod;
            this.detailLevel = detailLevel;
        }

        internal int AddVertex(Vector3 position, Vector3 normal, Vector2 coordinate)
        {
            int index = vertices.Count;
            vertices.Add(position);
            normals.Add(normal);
            coordinates.Add(coordinate);
            return index;
        }

        internal void Triangle(int surface, int first, int second, int third)
        {
            if (Vector3.Cross(vertices[second] - vertices[first], vertices[third] - vertices[first]).sqrMagnitude == 0)
            {
                return;
            }

            indices[surface].Add(first);
            indices[surface].Add(second);
            indices[surface].Add(third);
        }

        internal void Append(TrackMesh source, Vector3 offset)
        {
            int start = vertices.Count;
            foreach (var point in source.vertices)
            {
                vertices.Add(point + offset);
            }

            normals.AddRange(source.normals);
            coordinates.AddRange(source.coordinates);
            for (int surface = 0; surface < indices.Length; surface++)
            {
                foreach (int index in source.indices[surface])
                {
                    indices[surface].Add(start + index);
                }
            }
        }

        internal void Node(Vector3 center, Vector3 up, Vector3 forward, IList<Vector3> ends,
            IList<Vector3> endAxes = null, IList<Vector3> endUps = null)
        {
            if (ends.Count == 0)
            {
                if (forward.sqrMagnitude < 0.1f)
                {
                    forward = Vector3.Cross(up, Vector3.right).normalized;
                }

                Span(center - forward * geometry.IsolatedHalfLength, center + forward * geometry.IsolatedHalfLength,
                    up, up, forward, forward, true, true);
            }
            else if (ends.Count == 2)
            {
                Span(ends[0], ends[1], endUps?[0] ?? up, endUps?[1] ?? up,
                    endAxes == null ? ends[1] - ends[0] : -endAxes[0],
                    endAxes == null ? ends[1] - ends[0] : endAxes[1], false, false);
            }
            else
            {
                for (int index = 0; index < ends.Count; index++)
                {
                    Span(center, ends[index], up, endUps?[index] ?? up,
                        endAxes?[index] ?? ends[index] - center, endAxes?[index] ?? ends[index] - center,
                        ends.Count == 1, false);
                }
            }
        }

        private void Span(Vector3 start, Vector3 end, Vector3 startUp, Vector3 endUp,
            Vector3 startTangent, Vector3 endTangent, bool capStart, bool capEnd)
        {
            Path(new[] { start, end }, new[] { startTangent, endTangent }, new[] { startUp, endUp }, capStart, capEnd);
        }

        internal void Path(IList<Vector3> points, IList<Vector3> tangents, IList<Vector3> ups,
            bool capStart = false, bool capEnd = false)
        {
            if (points.Count < 2)
            {
                return;
            }

            var rows = new TubeRow[points.Count];
            float arcLength = 0;
            float previousMappedCoordinate = 0;
            float previousCoordinate = 0;
            for (int row = 0; row < points.Count; row++)
            {
                if (row > 0)
                {
                    arcLength += Vector3.Distance(points[row - 1], points[row]);
                }

                float mappedCoordinate = textureCoordinate == null
                    ? arcLength * 2 : textureCoordinate(points[row] + textureOrigin);
                float coordinate = mappedCoordinate;
                if (row > 0 && textureCoordinatePeriod > 0)
                {
                    float difference = mappedCoordinate - previousMappedCoordinate;
                    difference -= (float)Math.Round(difference / textureCoordinatePeriod) * textureCoordinatePeriod;
                    coordinate = previousCoordinate + difference;
                }

                rows[row] = new TubeRow(points[row], tangents[row], ups[row], coordinate);
                previousMappedCoordinate = mappedCoordinate;
                previousCoordinate = coordinate;
            }

            var retained = SimplifyRows(rows);
            int sides = detailLevel == 0 ? 32 : detailLevel == 1 ? 24 : 16;
            for (int layer = 0; layer < 2; layer++)
            {
                int first = vertices.Count;
                float layerRadius = radius * (layer == 0 ? 1 : 0.92f);
                for (int row = 0; row < retained.Count; row++)
                {
                    var frame = rows[retained[row]];
                    for (int column = 0; column <= sides; column++)
                    {
                        float angle = column * Mathf.PI * 2 / sides;
                        Vector3 normal = frame.Side * Mathf.Cos(angle) + frame.Radial * Mathf.Sin(angle);
                        AddVertex(frame.Position + normal * layerRadius, layer == 0 ? normal : -normal,
                            new Vector2((float)column / sides, frame.Coordinate));
                    }
                }

                int surface = layer == 0 ? 0 : 2;
                for (int row = 1; row < retained.Count; row++)
                {
                    for (int column = 0; column < sides; column++)
                    {
                        int upper = first + row * (sides + 1) + column;
                        int lower = upper - sides - 1;
                        if (layer == 0)
                        {
                            Triangle(surface, lower, lower + 1, upper);
                            Triangle(surface, lower + 1, upper + 1, upper);
                        }
                        else
                        {
                            Triangle(surface, lower, upper, lower + 1);
                            Triangle(surface, lower + 1, upper, upper + 1);
                        }
                    }
                }

                if (capStart)
                {
                    Cap(rows[0].Position, -rows[0].Tangent, first, sides, surface, layer != 0);
                }

                if (capEnd)
                {
                    int last = rows.Length - 1;
                    int lastRing = first + (retained.Count - 1) * (sides + 1);
                    Cap(rows[last].Position, rows[last].Tangent, lastRing, sides, surface, layer != 0);
                }
            }
        }

        private List<int> SimplifyRows(TubeRow[] rows)
        {
            float tolerance = detailLevel == 0 ? 0.002f : detailLevel == 1 ? 0.01f : 0.04f;
            var retained = new List<int> { 0 };
            int first = 0;
            while (first < rows.Length - 1)
            {
                int last = first + 1;
                for (int candidate = first + 2; candidate < rows.Length; candidate++)
                {
                    Vector3 span = rows[candidate].Position - rows[first].Position;
                    float lengthSquared = span.sqrMagnitude;
                    bool fits = lengthSquared > 0.0000001f;
                    for (int row = first + 1; fits && row < candidate; row++)
                    {
                        float fraction = Vector3.Dot(rows[row].Position - rows[first].Position, span) / lengthSquared;
                        if (fraction <= 0 || fraction >= 1)
                        {
                            fits = false;
                            break;
                        }

                        Vector3 position = Vector3.Lerp(rows[first].Position, rows[candidate].Position, fraction);
                        Vector3 side = Vector3.Lerp(rows[first].Side, rows[candidate].Side, fraction);
                        Vector3 radial = Vector3.Lerp(rows[first].Radial, rows[candidate].Radial, fraction);
                        // Center and frame errors bound every point on the outer circumference.
                        float frameError = (rows[row].Side - side).magnitude + (rows[row].Radial - radial).magnitude;
                        float outlineError = Vector3.Distance(rows[row].Position, position) + radius * frameError;
                        fits = outlineError <= tolerance;
                    }
                    if (!fits)
                    {
                        break;
                    }

                    last = candidate;
                }

                retained.Add(last);
                first = last;
            }

            return retained;
        }

        private void Cap(Vector3 center, Vector3 normal, int ring, int sides, int surface, bool reverse)
        {
            int middle = AddVertex(center, reverse ? -normal : normal, new Vector2(0.5f, 0.5f));
            for (int column = 0; column < sides; column++)
            {
                int first = AddVertex(vertices[ring + column], reverse ? -normal : normal, coordinates[ring + column]);
                int second = AddVertex(vertices[ring + column + 1], reverse ? -normal : normal,
                    coordinates[ring + column + 1]);
                if (Vector3.Dot(Vector3.Cross(vertices[first] - center, vertices[second] - center),
                    reverse ? -normal : normal) >= 0)
                {
                    Triangle(surface, middle, first, second);
                }
                else
                {
                    Triangle(surface, middle, second, first);
                }
            }
        }

        internal Mesh Build(string name)
        {
            var mesh = new Mesh
            {
                name = name,
                indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, coordinates);
            var colors = new Color32[vertices.Count];
            for (int index = 0; index < colors.Length; index++)
            {
                colors[index] = new Color32(255, 255, 255, 255);
            }

            mesh.colors32 = colors;
            mesh.subMeshCount = 5;
            for (int surface = 0; surface < indices.Length; surface++)
            {
                mesh.SetTriangles(indices[surface], surface);
            }

            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }
    }
}
