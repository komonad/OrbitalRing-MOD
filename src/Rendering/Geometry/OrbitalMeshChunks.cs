using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalMeshChunks
    {
        private static int buildFrame = -1;
        private static long buildTicks;

        internal static bool HasBuildBudget
        {
            get
            {
                if (buildFrame != Time.frameCount)
                {
                    buildFrame = Time.frameCount;
                    buildTicks = 0;
                }

                return buildTicks < Stopwatch.Frequency * 0.002;
            }
        }

        internal static void RecordBuildTime(long started)
        {
            buildTicks += Stopwatch.GetTimestamp() - started;
        }

        private sealed class ChunkBuilder
        {
            internal readonly Dictionary<int, int> VertexIndices = new Dictionary<int, int>();
            internal readonly List<Vector3> Positions = new List<Vector3>();
            internal readonly List<Vector3> Normals = new List<Vector3>();
            internal readonly List<Vector2> Coordinates = new List<Vector2>();
            internal readonly List<Vector4> Tangents = new List<Vector4>();
            internal readonly List<int> Triangles = new List<int>();
        }

        internal static IEnumerable<object> Create(Mesh source, List<Mesh> meshes, float cellSize = 64)
        {
            Vector3[] positions = source.vertices;
            yield return null;
            Vector3[] normals = source.normals;
            yield return null;
            Vector2[] coordinates = source.uv;
            yield return null;
            Vector4[] tangents = source.tangents;
            yield return null;
            int[] triangles = source.triangles;
            yield return null;
            bool hasTangents = tangents.Length == positions.Length;
            var chunks = new Dictionary<Vector3Int, ChunkBuilder>();
            for (int index = 0; index < triangles.Length; index += 3)
            {
                Vector3 center = (positions[triangles[index]] + positions[triangles[index + 1]] +
                    positions[triangles[index + 2]]) / 3;
                var cell = new Vector3Int(Mathf.FloorToInt(center.x / cellSize),
                    Mathf.FloorToInt(center.y / cellSize), Mathf.FloorToInt(center.z / cellSize));
                if (!chunks.TryGetValue(cell, out ChunkBuilder chunk))
                {
                    chunk = new ChunkBuilder();
                    chunks.Add(cell, chunk);
                }

                for (int corner = 0; corner < 3; corner++)
                {
                    int sourceIndex = triangles[index + corner];
                    if (!chunk.VertexIndices.TryGetValue(sourceIndex, out int chunkIndex))
                    {
                        chunkIndex = chunk.Positions.Count;
                        chunk.VertexIndices.Add(sourceIndex, chunkIndex);
                        chunk.Positions.Add(positions[sourceIndex]);
                        chunk.Normals.Add(normals[sourceIndex]);
                        chunk.Coordinates.Add(coordinates[sourceIndex]);
                        if (hasTangents)
                        {
                            chunk.Tangents.Add(tangents[sourceIndex]);
                        }
                    }

                    chunk.Triangles.Add(chunkIndex);
                }

                if ((index + 3) % (256 * 3) == 0)
                {
                    yield return null;
                }
            }

            int meshIndex = 0;
            foreach (ChunkBuilder chunk in chunks.Values)
            {
                var mesh = new Mesh
                {
                    name = source.name + ".Chunk." + meshIndex,
                    indexFormat = chunk.Positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                };
                meshes.Add(mesh);
                mesh.SetVertices(chunk.Positions);
                mesh.SetNormals(chunk.Normals);
                mesh.SetUVs(0, chunk.Coordinates);
                if (hasTangents)
                {
                    mesh.SetTangents(chunk.Tangents);
                }

                mesh.SetTriangles(chunk.Triangles, 0);
                mesh.RecalculateBounds();
                // Vertex channels retain the source frame so texture seams stay continuous across cells.
                mesh.UploadMeshData(true);
                meshIndex++;
                yield return null;
            }
        }

        internal static void Destroy(IEnumerable<Mesh> meshes)
        {
            if (meshes == null)
            {
                return;
            }

            foreach (Mesh mesh in meshes)
            {
                Object.Destroy(mesh);
            }
        }
    }
}
