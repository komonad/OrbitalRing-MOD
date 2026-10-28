using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectOrbitalRing.Rendering
{
    internal static partial class TrackRenderer
    {
        private const float DistantChunkSize = 128;
        private const float FullDetailRadiusPixels = 24;
        private const float MiddleDetailRadiusPixels = 6;
        private const float MergeTangentDotThreshold = -0.995f;
        private static bool distantMembershipDirty = true;
        private static bool prewarmRequestsDirty = true;
        private const double StructureBuildBudgetMilliseconds = 2;
        private static int urgentBuildUntilFrame;
        private static readonly LinkedList<DetailBuild> detailBuildQueue = new LinkedList<DetailBuild>();
        internal static bool StructureMeshesReady
        {
            get
            {
                foreach (var entry in distantChunks)
                {
                    if (NeedsDetailBuild(entry.Value.Meshes, 2))
                    {
                        return false;
                    }

                    foreach (var child in entry.Value.Children)
                    {
                        if (NeedsDetailBuild(child.Value, 1))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
        }

        internal static long ShadowTriangles { get; private set; }
        internal static int ShadowDrawCalls { get; private set; }

        private sealed class DetailBuild
        {
            internal Chunk Chunk;
            internal int Level;
            internal IEnumerator<object> Work;
            internal bool Completed;
            internal LinkedListNode<DetailBuild> QueueNode;
        }

        private static DetailBuild RequestDetailBuild(PlanetFactory factory, ChunkKey key, Chunk chunk,
            int level, bool urgent = false)
        {
            if (urgent)
            {
                urgentBuildUntilFrame = Time.frameCount + 2;
            }

            if (!chunk.PendingDetails.TryGetValue(level, out var pending))
            {
                pending = new DetailBuild
                {
                    Chunk = chunk,
                    Level = level,
                    Work = BuildDetailMeshes(factory, key, chunk, level).GetEnumerator(),
                };
                chunk.PendingDetails.Add(level, pending);
                pending.QueueNode = urgent ? detailBuildQueue.AddFirst(pending) : detailBuildQueue.AddLast(pending);
            }
            else if (urgent && pending.QueueNode?.List != null)
            {
                detailBuildQueue.Remove(pending.QueueNode);
                detailBuildQueue.AddFirst(pending.QueueNode);
            }

            return pending;
        }

        private static void CompleteDetailBuild(DetailBuild pending)
        {
            pending.Completed = true;
            pending.Work.Dispose();
            if (pending.Chunk.PendingDetails.TryGetValue(pending.Level, out var currentBuild) &&
                ReferenceEquals(currentBuild, pending))
            {
                pending.Chunk.PendingDetails.Remove(pending.Level);
            }
            if (pending.QueueNode?.List != null)
            {
                detailBuildQueue.Remove(pending.QueueNode);
            }
        }

        private static bool NeedsDetailBuild(Chunk chunk, int level) =>
            !chunk.DetailMeshes.TryGetValue(level, out var meshes) || meshes.Revision != chunk.Revision;

        internal static void PrewarmStructureMeshes(PlanetFactory factory, Camera camera)
        {
            if (factory?.cargoTraffic == null || camera == null)
            {
                return;
            }

            Initialize(factory);
            if (distantMembershipDirty || prewarmRequestsDirty)
            {
                foreach (var entry in RenderChunks(camera))
                {
                }

                foreach (var entry in distantChunks)
                {
                    if (NeedsDetailBuild(entry.Value.Meshes, 2))
                    {
                        RequestDetailBuild(factory, entry.Key, entry.Value.Meshes, 2);
                    }
                }

                foreach (var entry in chunks)
                {
                    if (NeedsDetailBuild(entry.Value, 1))
                    {
                        RequestDetailBuild(factory, entry.Key, entry.Value, 1);
                    }
                }

                prewarmRequestsDirty = false;
            }

            if (detailBuildQueue.Count == 0)
            {
                return;
            }

            var clock = Stopwatch.StartNew();
            double budget = Time.frameCount <= urgentBuildUntilFrame ? 4 : StructureBuildBudgetMilliseconds;
            while (detailBuildQueue.Count > 0 && clock.Elapsed.TotalMilliseconds < budget)
            {
                var pending = detailBuildQueue.First.Value;
                if (pending.Completed)
                {
                    detailBuildQueue.RemoveFirst();
                    continue;
                }

                if (!pending.Work.MoveNext())
                {
                    CompleteDetailBuild(pending);
                }
            }
        }

        private static readonly Dictionary<ChunkKey, DistantChunk> distantChunks =
            new Dictionary<ChunkKey, DistantChunk>();

        private sealed class DistantChunk
        {
            internal Chunk Meshes = new Chunk
            {
                Dirty = false
            };
            internal readonly Dictionary<int, NodeShape> Shapes = new Dictionary<int, NodeShape>();
            internal readonly List<KeyValuePair<ChunkKey, Chunk>> Children = new List<KeyValuePair<ChunkKey, Chunk>>();
        }

        private static void ClearDistantChunks()
        {
            foreach (var group in distantChunks.Values)
            {
                FreeDetailMeshes(group.Meshes);
            }

            distantChunks.Clear();
            detailBuildQueue.Clear();
            distantMembershipDirty = true;
            prewarmRequestsDirty = true;
        }

        private static IEnumerable<KeyValuePair<ChunkKey, Chunk>> RenderChunks(Camera camera)
        {
            if (distantMembershipDirty)
            {
                var grouped = new Dictionary<ChunkKey, DistantChunk>();
                foreach (var entry in chunks)
                {
                    Vector3 center = Origin(entry.Key) + Vector3.one * StructureChunkSize * 0.5f;
                    var cell = new Vector3Int(
                        Mathf.FloorToInt(center.x / DistantChunkSize), Mathf.FloorToInt(center.y / DistantChunkSize),
                        Mathf.FloorToInt(center.z / DistantChunkSize));
                    var key = new ChunkKey(cell, entry.Key.ItemId, entry.Key.Group, true);
                    if (!grouped.TryGetValue(key, out var group))
                    {
                        grouped.Add(key, group = new DistantChunk());
                    }

                    group.Children.Add(entry);
                    bool changed = entry.Value.Shapes.Count != entry.Value.Entities.Count;
                    foreach (int entityId in entry.Value.Entities)
                    {
                        var shape = GetNodeGeometry(current, entityId).Shape;
                        group.Meshes.Entities.Add(entityId);
                        group.Meshes.Representative = entityId;
                        group.Shapes.Add(entityId, shape);
                        changed |= !entry.Value.Shapes.TryGetValue(entityId, out var previousShape) ||
                            !ReferenceEquals(previousShape, shape);
                    }
                    if (changed)
                    {
                        InvalidateDetailMeshes(entry.Value);
                        entry.Value.Shapes.Clear();
                        foreach (int entityId in entry.Value.Entities)
                        {
                            entry.Value.Shapes.Add(entityId, group.Shapes[entityId]);
                        }
                    }
                }

                foreach (var entry in grouped)
                {
                    if (distantChunks.TryGetValue(entry.Key, out var previous))
                    {
                        var next = entry.Value.Meshes;
                        entry.Value.Meshes = previous.Meshes;
                        if (!SameShapes(previous.Shapes, entry.Value.Shapes))
                        {
                            InvalidateDetailMeshes(previous.Meshes);
                            previous.Meshes.Entities.Clear();
                            previous.Meshes.Entities.UnionWith(next.Entities);
                            previous.Meshes.Representative = next.Representative;
                        }
                        distantChunks.Remove(entry.Key);
                    }
                }

                foreach (var previous in distantChunks.Values)
                {
                    FreeDetailMeshes(previous.Meshes);
                }

                distantChunks.Clear();
                foreach (var entry in grouped)
                {
                    distantChunks.Add(entry.Key, entry.Value);
                }

                distantMembershipDirty = false;
                prewarmRequestsDirty = true;
            }

            foreach (var entry in distantChunks)
            {
                var group = entry.Value;
                var dimensions = GetDefinition(entry.Key.ItemId).Geometry;
                var bounds = new Bounds(Origin(entry.Key) + Vector3.one * DistantChunkSize * 0.5f,
                    Vector3.one * (DistantChunkSize + 8 + dimensions.CenterHeight + dimensions.Radius * 2));
                if (group.Meshes.HasBounds)
                {
                    bounds = group.Meshes.Bounds;
                    bounds.center += Origin(entry.Key);
                }

                if (StructureDetailLevel(camera, bounds, dimensions, GetDefinition(entry.Key.ItemId)) == 2)
                {
                    yield return new KeyValuePair<ChunkKey, Chunk>(entry.Key, group.Meshes);
                }
                else
                {
                    foreach (var child in group.Children)
                    {
                        yield return child;
                    }
                }
            }
        }

        private sealed class StructureLodMeshes
        {
            internal int Revision;
            internal Mesh Wall;
            internal Mesh Core;
            internal int ShadowSubmesh = -1;
            internal Material ShadowMaterial;
        }

        private static void DrawStructureShadows(PlanetFactory factory, Camera camera, int itemId)
        {
            ShadowTriangles = 0;
            ShadowDrawCalls = 0;
            foreach (var entry in distantChunks)
            {
                if ((itemId > 0 && entry.Key.ItemId != itemId) ||
                    !entry.Value.Meshes.DetailMeshes.TryGetValue(2, out var meshes))
                {
                    continue;
                }

                var surfaces = TrackMaterialLibrary.Get(entry.Key.ItemId);
                Matrix4x4 transform = Matrix4x4.Translate(Origin(entry.Key));
                if (meshes.ShadowSubmesh >= 0)
                {
                    SubmitStructureShadow(meshes.Core, meshes.ShadowMaterial, meshes.ShadowSubmesh, transform, camera);
                }
                else
                {
                    SubmitStructureShadow(meshes.Core, surfaces.Core, 0, transform, camera);
                    SubmitStructureShadow(meshes.Core, surfaces.Fittings, 1, transform, camera);
                    SubmitStructureShadow(meshes.Core, surfaces.Detail, 4, transform, camera);
                }
            }
        }

        private static bool CastsOpaqueShadows(Material material) => material != null &&
            material.renderQueue < 3000 && material.GetTag("RenderType", false) != "Transparent";

        private static void AddShadowSubmesh(StructureLodMeshes meshes, int itemId)
        {
            Mesh mesh = meshes.Core;
            if (mesh == null)
            {
                return;
            }

            var surfaces = TrackMaterialLibrary.Get(itemId);
            Material[] materials = { surfaces.Core, surfaces.Fittings, surfaces.Detail };
            int[] sourceSubmeshes = { 0, 1, 4 };
            var shadowIndices = new List<int>();
            Material shadowMaterial = null;
            for (int index = 0; index < sourceSubmeshes.Length; index++)
            {
                int submesh = sourceSubmeshes[index];
                Material material = materials[index];
                if (submesh >= mesh.subMeshCount || mesh.GetIndexCount(submesh) == 0 || !CastsOpaqueShadows(material))
                {
                    continue;
                }

                if (material.shader == null || material.shader.name != "Standard" ||
                    material.IsKeywordEnabled("_ALPHATEST_ON"))
                {
                    return;
                }

                shadowIndices.AddRange(mesh.GetTriangles(submesh));
                shadowMaterial = shadowMaterial ?? material;
            }

            if (shadowIndices.Count == 0)
            {
                return;
            }

            // Opaque Standard surfaces share one shadow pass and retain the original vertex channels.
            int shadowSubmesh = mesh.subMeshCount;
            mesh.subMeshCount = shadowSubmesh + 1;
            mesh.SetTriangles(shadowIndices, shadowSubmesh, false);
            meshes.ShadowSubmesh = shadowSubmesh;
            meshes.ShadowMaterial = shadowMaterial;
        }

        private static void SubmitStructureShadow(Mesh mesh, Material material, int submesh,
            Matrix4x4 transform, Camera camera)
        {
            if (mesh == null || !CastsOpaqueShadows(material) || submesh >= mesh.subMeshCount ||
                mesh.GetIndexCount(submesh) == 0)
            {
                return;
            }

            // 阴影使用固定细节级别，并由光源的剔除结果确定投影范围。
            ShadowTriangles += (long)mesh.GetIndexCount(submesh) / 3;
            ShadowDrawCalls++;
            Graphics.DrawMesh(mesh, transform, material, 0, camera, submesh, null,
                ShadowCastingMode.ShadowsOnly, false);
        }

        private static bool SameShapes(Dictionary<int, NodeShape> first, Dictionary<int, NodeShape> second)
        {
            // 形状快照在几何变化时替换；相同引用保留已生成的远景网格。
            if (first.Count != second.Count)
            {
                return false;
            }

            foreach (var entry in first)
            {
                if (!second.TryGetValue(entry.Key, out var shape) || !ReferenceEquals(entry.Value, shape))
                {
                    return false;
                }
            }

            return true;
        }

        private static int StructureDetailLevel(Camera camera, Bounds bounds,
            TrackGeometryProfile dimensions, TrackDefinition definition)
        {
            float pixelsPerUnit;
            if (camera.orthographic)
            {
                pixelsPerUnit = camera.pixelHeight / (2 * camera.orthographicSize);
            }
            else
            {
                Vector3 forward = camera.transform.forward;
                Vector3 extents = bounds.extents;
                float halfDepth = Mathf.Abs(forward.x) * extents.x + Mathf.Abs(forward.y) * extents.y +
                    Mathf.Abs(forward.z) * extents.z;
                float depth = Vector3.Dot(bounds.center - camera.transform.position, forward) - halfDepth;
                if (depth <= 0)
                {
                    return 0;
                }

                pixelsPerUnit = camera.pixelHeight / (2 * depth * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f));
            }

            float projectedRadius = dimensions.Radius * pixelsPerUnit;
            return projectedRadius >= FullDetailRadiusPixels ? 0 : projectedRadius >= MiddleDetailRadiusPixels ? 1 : 2;
        }

        private static void CancelDetailBuilds(Chunk chunk)
        {
            foreach (var pending in chunk.PendingDetails.Values)
            {
                pending.Completed = true;
                pending.Work.Dispose();
                if (pending.QueueNode?.List != null)
                {
                    detailBuildQueue.Remove(pending.QueueNode);
                }
            }
            chunk.PendingDetails.Clear();
        }

        private static void InvalidateDetailMeshes(Chunk chunk)
        {
            prewarmRequestsDirty = true;
            chunk.Revision++;
            chunk.Dirty = true;
            CancelDetailBuilds(chunk);
        }

        private static void DestroyDetailMeshes(StructureLodMeshes meshes)
        {
            if (meshes.Wall != null)
            {
                UnityEngine.Object.Destroy(meshes.Wall);
            }
            if (meshes.Core != null)
            {
                UnityEngine.Object.Destroy(meshes.Core);
            }
        }

        private static void FreeDetailMeshes(Chunk chunk)
        {
            prewarmRequestsDirty = true;
            CancelDetailBuilds(chunk);
            foreach (var meshes in chunk.DetailMeshes.Values)
            {
                DestroyDetailMeshes(meshes);
            }
            chunk.DetailMeshes.Clear();
        }

        private static void PublishDetailMeshes(Chunk chunk, int level, StructureLodMeshes replacement)
        {
            // Keep the previous generation drawable until a complete replacement is available.
            for (int previousLevel = 0; previousLevel <= 2; previousLevel++)
            {
                if (chunk.DetailMeshes.TryGetValue(previousLevel, out var previous) &&
                    (previousLevel == level || previous.Revision != chunk.Revision))
                {
                    DestroyDetailMeshes(previous);
                    chunk.DetailMeshes.Remove(previousLevel);
                }
            }
            chunk.DetailMeshes.Add(level, replacement);
            chunk.Dirty = false;
            chunk.HasBounds = false;
            foreach (var meshes in chunk.DetailMeshes.Values)
            {
                foreach (var mesh in new[] { meshes.Wall, meshes.Core })
                {
                    if (mesh == null)
                    {
                        continue;
                    }
                    if (chunk.HasBounds)
                    {
                        chunk.Bounds.Encapsulate(mesh.bounds);
                    }
                    else
                    {
                        chunk.Bounds = mesh.bounds;
                        chunk.HasBounds = true;
                    }
                }
            }
            if (chunk.HasBounds)
            {
                chunk.Bounds.Expand(0.2f);
            }
        }

        private static StructureLodMeshes GetDetailMeshes(PlanetFactory factory, ChunkKey key,
            Chunk chunk, int detailLevel)
        {
            if (!chunk.DetailMeshes.TryGetValue(detailLevel, out var cached))
            {
                var pending = RequestDetailBuild(factory, key, chunk, detailLevel);
                while (pending.Work.MoveNext())
                {
                }

                CompleteDetailBuild(pending);
                cached = chunk.DetailMeshes[detailLevel];
            }

            return cached;
        }

        private static IEnumerable<object> BuildDetailMeshes(PlanetFactory factory, ChunkKey key,
            Chunk chunk, int detailLevel)
        {
            int revision = chunk.Revision;
            Vector3 origin = Origin(key);
            var definition = GetDefinition(key.ItemId);
            var appearance = definition.Appearance;
            bool glassWalls = appearance.WallMode == TrackWallMode.Glass;
            var walls = new TrackMesh(definition.Geometry.Radius * appearance.WallRadiusFraction,
                TrackMaterialLibrary.TextureCoordinate(key.ItemId, factory), origin, definition.Geometry,
                appearance.TextureCoordinatePeriod, detailLevel);
            var core = new TrackMesh();
            var eligible = new HashSet<int>();
            foreach (int entityId in chunk.Entities)
            {
                chunk.Representative = entityId;
                var shape = GetNodeGeometry(factory, entityId).Shape;
                if (shape.Ends.Count == 2 && TrackTopology.HasOneInputAndOutput(factory, entityId) &&
                    Vector3.Dot((shape.Ends[0] - shape.Center).normalized,
                        (shape.Ends[1] - shape.Center).normalized) < MergeTangentDotThreshold)
                {
                    eligible.Add(entityId);
                }
                else
                {
                    if (glassWalls)
                    {
                        walls.Append(NodeMesh(factory, entityId, detailLevel),
                            factory.entityPool[entityId].pos - origin);
                    }

                    foreach (var step in AppendNodeModel(factory, key, core, entityId, detailLevel))
                        yield return step;
                }

                yield return null;
            }

            {
                var adjacency = new Dictionary<int, List<int>>();
                foreach (int entityId in eligible)
                {
                    var neighbors = new List<int>();
                    foreach (int neighbor in GetNodeGeometry(factory, entityId).Shape.Neighbors)
                    {
                        if (eligible.Contains(neighbor))
                        {
                            neighbors.Add(neighbor);
                        }
                    }

                    adjacency.Add(entityId, neighbors);
                }

                var visited = new HashSet<int>();
                foreach (int entityId in eligible)
                {
                    if (adjacency[entityId].Count < 2)
                    {
                        foreach (var step in AppendModelChain(factory, key, core, walls, adjacency, visited,
                            entityId, detailLevel))
                        {
                            yield return step;
                        }
                    }
                }

                foreach (int entityId in eligible)
                {
                    foreach (var step in AppendModelChain(factory, key, core, walls, adjacency, visited,
                        entityId, detailLevel))
                    {
                        yield return step;
                    }
                }
            }

            var cached = new StructureLodMeshes
            {
                Revision = revision,
                Wall = walls.VertexCount == 0 ? null : walls.Build("OrbitalRing.TrackLOD.Wall"),
                Core = core.VertexCount == 0 ? null : core.Build("OrbitalRing.TrackLOD.Core"),
            };
            if (revision != chunk.Revision)
            {
                DestroyDetailMeshes(cached);
                yield break;
            }
            if (key.Distant && detailLevel == 2)
            {
                AddShadowSubmesh(cached, key.ItemId);
            }
            PublishDetailMeshes(chunk, detailLevel, cached);
            Rebuilds++;
            yield break;
        }

        private static IEnumerable<object> AppendModelChain(PlanetFactory factory, ChunkKey key, TrackMesh target,
            TrackMesh walls, Dictionary<int, List<int>> adjacency, HashSet<int> visited, int start, int detailLevel)
        {
            if (visited.Contains(start))
            {
                yield break;
            }

            var chain = new List<int>();
            int currentEntity = start;
            while (visited.Add(currentEntity))
            {
                chain.Add(currentEntity);
                int next = 0;
                foreach (int neighbor in adjacency[currentEntity])
                {
                    if (!visited.Contains(neighbor))
                    {
                        next = neighbor;
                        break;
                    }
                }

                if (next == 0)
                {
                    break;
                }

                currentEntity = next;
            }

            var first = GetNodeGeometry(factory, chain[0]).Shape;
            if (chain.Count > 1)
            {
                Vector3 nextCenter = GetNodeGeometry(factory, chain[1]).Shape.Center + factory.entityPool[chain[1]].pos;
                Vector3 direction = nextCenter - (first.Center + factory.entityPool[chain[0]].pos);
                if (Vector3.Dot(first.Ends[first.OutputEnd] - first.Center, direction) < 0)
                {
                    chain.Reverse();
                    first = GetNodeGeometry(factory, chain[0]).Shape;
                }
            }

            Vector3 origin = Origin(key);
            var points = new List<Vector3>();
            var axes = new List<Vector3>();
            var frames = new List<Vector3>();
            for (int index = 0; index < chain.Count; index++)
            {
                int entityId = chain[index];
                var shape = GetNodeGeometry(factory, entityId).Shape;
                Vector3 offset = factory.entityPool[entityId].pos - origin;
                if (index == 0 || index == chain.Count - 1)
                {
                    int otherEntity = chain.Count == 1 ? 0 : chain[index == 0 ? 1 : index - 1];
                    Vector3 towardOther = otherEntity == 0 ? Vector3.zero :
                        GetNodeGeometry(factory, otherEntity).Shape.Center + factory.entityPool[otherEntity].pos
                        - (shape.Center + factory.entityPool[entityId].pos);
                    int outside = otherEntity == 0 ? 1 - shape.OutputEnd :
                        (Vector3.Dot(shape.Ends[0] - shape.Ends[1], towardOther) < 0 ? 0 : 1);
                    if (index == 0)
                    {
                        points.Add(shape.Ends[outside] + offset);
                        axes.Add(-shape.EndAxes[outside]);
                        frames.Add(shape.EndUps[outside]);
                    }

                    points.Add(shape.Center + offset);
                    axes.Add((shape.Ends[1 - outside] - shape.Ends[outside]).normalized * (index == 0 ? 1 : -1));
                    frames.Add(shape.Up);
                    if (index == chain.Count - 1)
                    {
                        if (chain.Count == 1)
                        {
                            outside = 1 - outside;
                        }

                        points.Add(shape.Ends[outside] + offset);
                        axes.Add(shape.EndAxes[outside]);
                        frames.Add(shape.EndUps[outside]);
                    }
                }
                else
                {
                    points.Add(shape.Center + offset);
                    Vector3 direction = factory.entityPool[chain[index + 1]].pos
                        - factory.entityPool[chain[index - 1]].pos;
                    axes.Add(direction.normalized);
                    frames.Add(shape.Up);
                }
            }

            if (GetDefinition(key.ItemId).Appearance.WallMode == TrackWallMode.Glass)
            {
                walls.Path(points, axes, frames);
            }

            foreach (var step in TrackModelWarp.Append(target, factory, key.ItemId, detailLevel,
                origin, points, axes, frames))
                yield return step;
        }

        private static IEnumerable<object> AppendNodeModel(PlanetFactory factory, ChunkKey key,
            TrackMesh target, int entityId, int detailLevel)
        {
            var shape = GetNodeGeometry(factory, entityId).Shape;
            Vector3 origin = Origin(key);
            Vector3 offset = factory.entityPool[entityId].pos - origin;
            if (shape.Ends.Count == 2)
            {
                foreach (var step in TrackModelWarp.Append(target, factory, key.ItemId, detailLevel, origin,
                    new[] { shape.Ends[0] + offset, shape.Center + offset, shape.Ends[1] + offset, },
                    new[] { -shape.EndAxes[0], (shape.Ends[1] - shape.Ends[0]).normalized, shape.EndAxes[1], },
                    new[] { shape.EndUps[0], shape.Up, shape.EndUps[1] }))
                    yield return step;
            }
            else if (shape.Ends.Count == 0)
            {
                Vector3 direction = shape.Forward.normalized;
                float halfLength = GetDefinition(key.ItemId).Geometry.IsolatedHalfLength;
                foreach (var step in TrackModelWarp.Append(target, factory, key.ItemId, detailLevel, origin,
                    new[] { shape.Center + offset - direction * halfLength,
                        shape.Center + offset + direction * halfLength, },
                    new[] { direction, direction }, new[] { shape.Up, shape.Up }))
                    yield return step;
            }
            else
            {
                for (int index = 0; index < shape.Ends.Count; index++)
                    foreach (var step in TrackModelWarp.Append(target, factory, key.ItemId, detailLevel, origin,
                        new[] { shape.Center + offset, shape.Ends[index] + offset },
                        new[] { shape.EndAxes[index], shape.EndAxes[index] }, new[] { shape.Up, shape.EndUps[index] }))
                        yield return step;
            }
        }
    }
}
