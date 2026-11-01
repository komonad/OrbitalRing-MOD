using System;
using System.Collections.Generic;
using ProjectOrbitalRing.Patches.Logic.OrbitalRing;
using ProjectOrbitalRing.Utils;
using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    internal static class TrackModelWarp
    {
        private const int TrianglesPerBuildStep = 256;

        private struct ModelVertex
        {
            internal Vector3 Position;
            internal Vector3 Normal;
            internal Vector2 Coordinate;

            internal static ModelVertex Lerp(ModelVertex first, ModelVertex second, float fraction) => new ModelVertex
            {
                Position = Vector3.LerpUnclamped(first.Position, second.Position, fraction),
                Normal = Vector3.LerpUnclamped(first.Normal, second.Normal, fraction),
                Coordinate = Vector2.LerpUnclamped(first.Coordinate, second.Coordinate, fraction),
            };
        }

        private readonly struct WarpFrame
        {
            internal readonly Vector3 Center;
            internal readonly Vector3 Tangent;
            internal readonly Vector3 Up;
            internal readonly Vector3 Side;
            internal readonly float InverseLongitudinalScale;

            internal WarpFrame(Vector3 center, Vector3 tangent, Vector3 up, Vector3 side,
                float inverseLongitudinalScale)
            {
                Center = center;
                Tangent = tangent;
                Up = up;
                Side = side;
                InverseLongitudinalScale = inverseLongitudinalScale;
            }
        }

        private sealed class Path
        {
            internal readonly List<Vector3> Points;
            internal readonly List<Vector3> Axes;
            internal readonly List<Vector3> Ups;
            internal readonly float[] Phases;
            internal readonly int Capacity;
            private readonly Dictionary<float, WarpFrame> frames = new Dictionary<float, WarpFrame>();

            internal Path(PlanetFactory factory, Vector3 origin,
                IList<Vector3> points, IList<Vector3> axes, IList<Vector3> ups)
            {
                Points = new List<Vector3>(points);
                Axes = new List<Vector3>(axes);
                Ups = new List<Vector3>(ups);
                Capacity = PosTool.GetLayout(factory.planet).StationCapacity;
                Phases = new float[points.Count];
                for (int index = 0; index < Phases.Length; index++)
                {
                    float phase = OrbitalTextureLayout.FacilityCoordinate(factory.planet, points[index] + origin);
                    if (index > 0)
                    {
                        phase += Mathf.Round((Phases[index - 1] - phase) / Capacity) * Capacity;
                    }

                    Phases[index] = phase;
                }

                if (Phases[Phases.Length - 1] < Phases[0])
                {
                    Array.Reverse(Phases);
                    Points.Reverse();
                    Axes.Reverse();
                    Ups.Reverse();
                    for (int index = 0; index < Axes.Count; index++)
                    {
                        Axes[index] = -Axes[index];
                    }
                }
            }

            internal void BeginCycle()
            {
                // The same model Z maps to a different path frame in each geographic period.
                frames.Clear();
            }

            internal int Add(TrackMesh target, ModelVertex vertex, float cycle, float referenceLength,
                float referenceCoordinateSpan, bool localCoordinates)
            {
                if (!frames.TryGetValue(vertex.Position.z, out var frame))
                {
                    frame = CreateFrame(vertex.Position.z, cycle, referenceLength);
                    frames.Add(vertex.Position.z, frame);
                }

                Vector3 crossSectionNormal = frame.Side * vertex.Normal.x + frame.Up * vertex.Normal.y;
                Vector3 longitudinalNormal = frame.Tangent * (vertex.Normal.z * frame.InverseLongitudinalScale);
                Vector3 normal = (crossSectionNormal + longitudinalNormal).normalized;
                Vector2 coordinate = vertex.Coordinate;
                // Continuous UVs follow the actual facility spacing; frame windows stay local.
                if (!localCoordinates)
                {
                    float facilityCoordinate = coordinate.y / referenceCoordinateSpan + cycle;
                    coordinate.y = facilityCoordinate * (OrbitalTextureLayout.CoordinatePeriod / Capacity);
                }

                Vector3 position = frame.Center + frame.Side * vertex.Position.x + frame.Up * vertex.Position.y;
                return target.AddVertex(position, normal, coordinate);
            }

            private WarpFrame CreateFrame(float modelLength, float cycle, float referenceLength)
            {
                float phase = cycle + modelLength / referenceLength;
                int lowerIndex = 0;
                int upperIndex = Phases.Length - 1;
                while (upperIndex - lowerIndex > 1)
                {
                    int middleIndex = (lowerIndex + upperIndex) / 2;
                    if (Phases[middleIndex] <= phase)
                    {
                        lowerIndex = middleIndex;
                    }
                    else
                    {
                        upperIndex = middleIndex;
                    }
                }

                float interval = Phases[upperIndex] - Phases[lowerIndex];
                float fraction = interval > 0.0000001f ? Mathf.Clamp01((phase - Phases[lowerIndex]) / interval) : 0;
                Vector3 center = Vector3.Lerp(Points[lowerIndex], Points[upperIndex], fraction);
                Vector3 tangent = Vector3.Lerp(Axes[lowerIndex], Axes[upperIndex], fraction).normalized;
                if (tangent.sqrMagnitude < 0.5f)
                {
                    tangent = (Points[upperIndex] - Points[lowerIndex]).normalized;
                }

                Vector3 up = Vector3.Lerp(Ups[lowerIndex], Ups[upperIndex], fraction).normalized;
                Vector3 side = Vector3.Cross(up, tangent).normalized;
                up = Vector3.Cross(tangent, side).normalized;
                float lengthScale = 1;
                if (interval > 0.0000001f)
                {
                    float segmentLength = Vector3.Distance(Points[lowerIndex], Points[upperIndex]);
                    lengthScale = segmentLength / (interval * referenceLength);
                }
                return new WarpFrame(center, tangent, up, side, 1f / Mathf.Max(0.0001f, lengthScale));
            }
        }

        internal static IEnumerable<object> Append(TrackMesh target, PlanetFactory factory, int itemId, int detailLevel,
            Vector3 origin, IList<Vector3> points, IList<Vector3> axes, IList<Vector3> ups)
        {
            if (points.Count < 2)
            {
                yield break;
            }

            var path = new Path(factory, origin, points, axes, ups);
            if (path.Phases[path.Phases.Length - 1] - path.Phases[0] < 0.0000001f)
            {
                yield break;
            }

            var model = TrackModelAssets.Get(itemId, detailLevel);
            var mapping = new int[model.Vertices.Length];
            var polygon = new ModelVertex[6];
            var clipped = new ModelVertex[6];
            int firstCycle = Mathf.FloorToInt(path.Phases[0]);
            int lastCycle = Mathf.CeilToInt(path.Phases[path.Phases.Length - 1]) - 1;
            for (int cycle = firstCycle; cycle <= lastCycle; cycle++)
            {
                path.BeginCycle();
                float minimumLength = (path.Phases[0] - cycle) * model.ReferenceArcLength;
                float maximumLength = (path.Phases[path.Phases.Length - 1] - cycle) * model.ReferenceArcLength;
                for (int surface = 0; surface < model.Indices.Length; surface++)
                {
                    var triangles = model.Indices[surface];
                    if (triangles.Length == 0)
                    {
                        continue;
                    }

                    Array.Clear(mapping, 0, mapping.Length);
                    bool localCoordinates = itemId == ProtoID.I粒子加速轨道 && (surface == 1 || surface == 4);
                    for (int triangle = 0; triangle < triangles.Length; triangle += 3)
                    {
                        if (triangle > 0 && triangle % (TrianglesPerBuildStep * 3) == 0)
                        {
                            yield return null;
                        }

                        int first = triangles[triangle];
                        int second = triangles[triangle + 1];
                        int third = triangles[triangle + 2];
                        float firstLength = model.Vertices[first].z;
                        float secondLength = model.Vertices[second].z;
                        float thirdLength = model.Vertices[third].z;
                        if (Mathf.Max(firstLength, Mathf.Max(secondLength, thirdLength)) < minimumLength ||
                            Mathf.Min(firstLength, Mathf.Min(secondLength, thirdLength)) > maximumLength)
                        {
                            continue;
                        }

                        if (firstLength >= minimumLength && firstLength <= maximumLength &&
                            secondLength >= minimumLength && secondLength <= maximumLength &&
                            thirdLength >= minimumLength && thirdLength <= maximumLength)
                        {
                            int firstIndex = Map(first);
                            int secondIndex = Map(second);
                            int thirdIndex = Map(third);
                            target.Triangle(surface, firstIndex, secondIndex, thirdIndex);
                        }
                        else
                        {
                            polygon[0] = Read(first);
                            polygon[1] = Read(second);
                            polygon[2] = Read(third);
                            int count = Clip(polygon, 3, clipped, minimumLength, true);
                            count = Clip(clipped, count, polygon, maximumLength, false);
                            if (count < 3)
                            {
                                continue;
                            }

                            int anchor = path.Add(target, polygon[0], cycle,
                                model.ReferenceArcLength, model.ReferenceCoordinateSpan, localCoordinates);
                            int previous = path.Add(target, polygon[1], cycle,
                                model.ReferenceArcLength, model.ReferenceCoordinateSpan, localCoordinates);
                            for (int corner = 2; corner < count; corner++)
                            {
                                int next = path.Add(target, polygon[corner], cycle,
                                    model.ReferenceArcLength, model.ReferenceCoordinateSpan, localCoordinates);
                                // A boundary intersection can repeat a corner at floating-point precision.
                                Vector3 firstEdge = polygon[corner - 1].Position - polygon[0].Position;
                                Vector3 secondEdge = polygon[corner].Position - polygon[0].Position;
                                if (Vector3.Cross(firstEdge, secondEdge).sqrMagnitude > 1e-16f)
                                {
                                    target.Triangle(surface, anchor, previous, next);
                                }

                                previous = next;
                            }
                        }
                    }

                    yield return null;

                    ModelVertex Read(int index) => new ModelVertex
                    {
                        Position = model.Vertices[index],
                        Normal = model.Normals[index],
                        Coordinate = model.Coordinates[index],
                    };
                    int Map(int index)
                    {
                        if (mapping[index] == 0)
                        {
                            mapping[index] = path.Add(target, Read(index), cycle,
                                model.ReferenceArcLength, model.ReferenceCoordinateSpan, localCoordinates) + 1;
                        }

                        return mapping[index] - 1;
                    }
                }
            }
        }

        private static int Clip(ModelVertex[] input, int count, ModelVertex[] output, float limit, bool keepGreater)
        {
            if (count == 0)
            {
                return 0;
            }

            int outputCount = 0;
            var previous = input[count - 1];
            bool previousInside = keepGreater ? previous.Position.z >= limit : previous.Position.z <= limit;
            for (int index = 0; index < count; index++)
            {
                var current = input[index];
                bool currentInside = keepGreater ? current.Position.z >= limit : current.Position.z <= limit;
                if (previousInside != currentInside)
                {
                    float fraction = (limit - previous.Position.z) / (current.Position.z - previous.Position.z);
                    var intersection = ModelVertex.Lerp(previous, current, fraction);
                    intersection.Position.z = limit;
                    output[outputCount++] = intersection;
                }

                if (currentInside)
                {
                    output[outputCount++] = current;
                }

                previous = current;
                previousInside = currentInside;
            }

            return outputCount;
        }
    }
}
