using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectOrbitalRing.Rendering
{
    internal sealed class TrackDefinition
    {
        internal TrackAppearance Appearance { get; }
        internal TrackGeometryProfile Geometry { get; }
        internal Func<PlanetFactory, int, int> Group { get; }

        internal TrackDefinition(TrackAppearance appearance, TrackGeometryProfile geometry,
            Func<PlanetFactory, int, int> group)
        {
            Appearance = appearance ?? new TrackAppearance();
            Geometry = geometry ?? TrackGeometryProfile.Default;
            Group = group;
        }
    }

    internal static partial class TrackRenderer
    {
        private const float StructureChunkSize = 32;
        private static readonly Dictionary<int, TrackDefinition> definitions = new Dictionary<int, TrackDefinition>();
        private static readonly Dictionary<ChunkKey, Chunk> chunks = new Dictionary<ChunkKey, Chunk>();
        private static readonly Dictionary<int, ChunkKey> keys = new Dictionary<int, ChunkKey>();
        private static readonly Dictionary<int, NodeGeometry> geometry = new Dictionary<int, NodeGeometry>();
        private static readonly object changesLock = new object();
        private static Dictionary<PlanetFactory, HashSet<int>> changes = new Dictionary<PlanetFactory, HashSet<int>>();
        private static readonly HashSet<int> changedNodes = new HashSet<int>();
        private static readonly List<int> neighbors = new List<int>(4);
        private static readonly Dictionary<EffectKey, EffectState> effectStates =
            new Dictionary<EffectKey, EffectState>();
        private static PlanetFactory current;

        internal static event Action<PlanetFactory, int> ConnectionsChanged;
        internal static int Rebuilds { get; private set; }
        internal static int NodeRebuilds { get; private set; }

        internal static void Register(int itemId, TrackAppearance appearance, TrackGeometryProfile geometry,
            Func<PlanetFactory, int, int> group)
        {
            definitions.Add(itemId, new TrackDefinition(appearance, geometry, group));
        }

        internal static bool IsRegistered(int itemId) => definitions.ContainsKey(itemId);

        internal static TrackDefinition GetDefinition(int itemId) =>
            definitions.TryGetValue(itemId, out var definition) ? definition : null;

        internal static bool TryGetGeometry(int itemId, out TrackGeometryProfile dimensions)
        {
            var definition = GetDefinition(itemId);
            dimensions = definition?.Geometry;
            return definition != null;
        }

        private readonly struct ChunkKey : IEquatable<ChunkKey>
        {
            internal readonly Vector3Int Cell;
            internal readonly int ItemId;
            internal readonly int Group;
            internal readonly bool Distant;

            internal ChunkKey(Vector3Int cell, int itemId, int group, bool distant = false)
            {
                Cell = cell;
                ItemId = itemId;
                Group = group;
                Distant = distant;
            }

            public bool Equals(ChunkKey other) =>
                Cell == other.Cell && ItemId == other.ItemId && Group == other.Group && Distant == other.Distant;

            public override bool Equals(object other) => other is ChunkKey key && Equals(key);

            public override int GetHashCode() =>
                unchecked(((Cell.GetHashCode() * 397 ^ ItemId) * 397 ^ Group) * 397 ^ Distant.GetHashCode());
        }

        private readonly struct EffectKey : IEquatable<EffectKey>
        {
            private readonly int itemId;
            private readonly int group;
            private readonly TrackSurfaceRole role;

            internal EffectKey(int itemId, int group, TrackSurfaceRole role)
            {
                this.itemId = itemId;
                this.group = group;
                this.role = role;
            }

            public bool Equals(EffectKey other) => itemId == other.itemId && group == other.group && role == other.role;

            public override bool Equals(object other) => other is EffectKey key && Equals(key);

            public override int GetHashCode() => unchecked((itemId * 397 ^ group) * 397 ^ (int)role);
        }

        private sealed class EffectState
        {
            // Ring effects share one activation state and animation phase across chunks in the same group.
            internal readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();
            internal int Frame = -1;
            internal bool Applied;
        }

        private sealed class Chunk
        {
            internal readonly HashSet<int> Entities = new HashSet<int>();
            internal readonly Dictionary<int, NodeShape> Shapes = new Dictionary<int, NodeShape>();
            internal int Revision;
            internal readonly Dictionary<int, StructureLodMeshes> DetailMeshes =
                new Dictionary<int, StructureLodMeshes>();
            internal readonly Dictionary<int, DetailBuild> PendingDetails = new Dictionary<int, DetailBuild>();
            internal bool Dirty = true;
            internal Bounds Bounds;
            internal bool HasBounds;
            internal int Representative;
        }

        private sealed class NodeGeometry
        {
            internal int ItemId;
            internal TrackMesh Wall;
            internal NodeShape Shape;
            internal bool Dirty = true;
        }

        private sealed class NodeShape
        {
            internal Vector3 Position;
            internal Vector3 Center;
            internal Vector3 Up;
            internal Vector3 Forward;
            internal readonly List<Vector3> Ends = new List<Vector3>(4);
            internal readonly List<Vector3> EndAxes = new List<Vector3>(4);
            internal readonly List<Vector3> EndUps = new List<Vector3>(4);
            internal readonly List<int> Neighbors = new List<int>(4);
            internal int OutputEnd;

            internal bool Matches(NodeShape other)
            {
                if (other == null || !Position.Equals(other.Position) || !Center.Equals(other.Center) ||
                    !Up.Equals(other.Up) || OutputEnd != other.OutputEnd || Ends.Count != other.Ends.Count ||
                    (Ends.Count == 0 && !Forward.Equals(other.Forward)))
                {
                    return false;
                }

                for (int index = 0; index < Ends.Count; index++)
                {
                    if (!Ends[index].Equals(other.Ends[index]) || !EndAxes[index].Equals(other.EndAxes[index]) ||
                        !EndUps[index].Equals(other.EndUps[index]) || Neighbors[index] != other.Neighbors[index])
                    {
                        return false;
                    }
                }

                return true;
            }

            internal void AddTo(TrackMesh builder) => builder.Node(Center, Up, Forward, Ends, EndAxes, EndUps);
        }

        private static Vector3 Origin(ChunkKey key) =>
            new Vector3(key.Cell.x, key.Cell.y, key.Cell.z) * (key.Distant ? DistantChunkSize : StructureChunkSize);

        private static ChunkKey Key(PlanetFactory factory, int entityId)
        {
            var entity = factory.entityPool[entityId];
            Vector3 position = entity.pos;
            return new ChunkKey(new Vector3Int(
                Mathf.FloorToInt(position.x / StructureChunkSize), Mathf.FloorToInt(position.y / StructureChunkSize),
                Mathf.FloorToInt(position.z / StructureChunkSize)), entity.protoId,
                TrackMaterialLibrary.BatchGroup(factory, entityId));
        }

        internal static void Dirty(PlanetFactory factory, int entityId)
        {
            if (factory == null || entityId <= 0)
            {
                return;
            }

            lock (changesLock)
            {
                if (!changes.TryGetValue(factory, out var entities))
                {
                    changes.Add(factory, entities = new HashSet<int>());
                }

                entities.Add(entityId);
            }
        }

        private static void FlushChanges()
        {
            Dictionary<PlanetFactory, HashSet<int>> pending;
            lock (changesLock)
            {
                if (changes.Count == 0)
                {
                    return;
                }

                pending = changes;
                changes = new Dictionary<PlanetFactory, HashSet<int>>();
            }

            foreach (var entry in pending)
            {
                changedNodes.Clear();
                foreach (int entityId in entry.Value)
                {
                    changedNodes.Add(entityId);
                    if (entry.Key == current && geometry.TryGetValue(entityId, out var cached) && cached.Shape != null)
                    {
                        changedNodes.UnionWith(cached.Shape.Neighbors);
                    }

                    TrackTopology.CopyNeighbors(entry.Key, entityId, neighbors);
                    changedNodes.UnionWith(neighbors);
                }

                foreach (int entityId in changedNodes)
                {
                    if (entry.Key == current)
                    {
                        Mark(entityId);
                    }

                    ConnectionsChanged?.Invoke(entry.Key, entityId);
                }
            }

            changedNodes.Clear();
        }

        private static void Mark(int entityId)
        {
            distantMembershipDirty = true;
            if (geometry.TryGetValue(entityId, out var cached))
            {
                cached.Dirty = true;
            }

            if (!TrackTopology.IsNode(current, entityId))
            {
                geometry.Remove(entityId);
                if (keys.TryGetValue(entityId, out var removedKey))
                {
                    keys.Remove(entityId);
                    RemoveChunkEntity(removedKey, entityId);
                }

                return;
            }

            var key = Key(current, entityId);
            if (keys.TryGetValue(entityId, out var old) && !old.Equals(key))
            {
                RemoveChunkEntity(old, entityId);
            }

            if (!chunks.TryGetValue(key, out var chunk))
            {
                chunks.Add(key, chunk = new Chunk());
            }

            chunk.Entities.Add(entityId);
            keys[entityId] = key;
        }

        private static void RemoveChunkEntity(ChunkKey key, int entityId)
        {
            var chunk = chunks[key];
            chunk.Entities.Remove(entityId);
            if (chunk.Entities.Count == 0)
            {
                FreeDetailMeshes(chunk);
                chunks.Remove(key);
            }
        }

        private static void Initialize(PlanetFactory factory)
        {
            if (current == factory)
            {
                return;
            }

            FreeMeshes();
            current = factory;
            if (factory?.cargoTraffic == null)
            {
                return;
            }

            for (int beltId = 1; beltId < factory.cargoTraffic.beltCursor; beltId++)
            {
                if (factory.cargoTraffic.beltPool[beltId].id != beltId)
                {
                    continue;
                }

                int entityId = factory.cargoTraffic.beltPool[beltId].entityId;
                if (TrackTopology.IsNode(factory, entityId))
                {
                    factory.cargoTraffic.RemoveBeltRenderer(beltId);
                    Mark(entityId);
                }
            }

            for (int pathId = 1; pathId < factory.cargoTraffic.pathCursor; pathId++)
            {
                if (TrackRenderPatches.IsTrackPath(factory.cargoTraffic, pathId))
                {
                    factory.cargoTraffic.RemovePathRenderer(pathId);
                }
            }
        }

        private static NodeGeometry GetNodeGeometry(PlanetFactory factory, int entityId)
        {
            if (!geometry.TryGetValue(entityId, out var node))
            {
                geometry.Add(entityId, node = new NodeGeometry());
            }

            int itemId = factory.entityPool[entityId].protoId;
            if (node.ItemId != itemId)
            {
                node.ItemId = itemId;
                node.Wall = null;
                node.Dirty = true;
            }

            if (node.Dirty)
            {
                var shape = CreateShape(factory, entityId);
                if (!shape.Matches(node.Shape))
                {
                    node.Shape = shape;
                    node.Wall = null;
                }

                node.Dirty = false;
            }

            return node;
        }

        private static NodeShape CreateShape(PlanetFactory factory, int entityId)
        {
            var entity = factory.entityPool[entityId];
            var definition = GetDefinition(entity.protoId);
            var dimensions = definition.Geometry;
            Vector3 center = entity.pos + entity.pos.normalized * dimensions.CenterHeight;
            var shape = new NodeShape
            {
                Position = entity.pos,
                Center = center - entity.pos,
                Up = entity.pos.normalized,
                Forward = entity.rot * Vector3.forward,
                OutputEnd = -1,
            };
            for (int slot = 0; slot < 4; slot++)
            {
                if (!TrackTopology.TryReadLink(factory, entityId, slot, out bool output, out int other) ||
                    shape.Neighbors.Contains(other))
                {
                    continue;
                }

                var peer = factory.entityPool[other];
                var peerDimensions = GetDefinition(peer.protoId).Geometry;
                Vector3 peerCenter = peer.pos + peer.pos.normalized * peerDimensions.CenterHeight;
                Vector3 end = (center + peerCenter) * 0.5f;
                if ((end - center).sqrMagnitude < 0.00001f)
                {
                    continue;
                }

                shape.Neighbors.Add(other);
                shape.Ends.Add(end - entity.pos);
                shape.EndAxes.Add((end - center).normalized);
                shape.EndUps.Add(end.normalized);
                if (output)
                {
                    shape.OutputEnd = shape.Ends.Count - 1;
                }
            }

            return shape;
        }

        private static TrackMesh NodeMesh(PlanetFactory factory, int entityId, int detailLevel = 0)
        {
            var node = GetNodeGeometry(factory, entityId);
            if (detailLevel == 0 && node.Wall != null)
                return node.Wall;
            var definition = GetDefinition(node.ItemId);
            var appearance = definition.Appearance;
            var mesh = new TrackMesh(definition.Geometry.Radius * appearance.WallRadiusFraction,
                TrackMaterialLibrary.TextureCoordinate(node.ItemId, factory), factory.entityPool[entityId].pos,
                definition.Geometry, appearance.TextureCoordinatePeriod, detailLevel);
            if (appearance.WallMode == TrackWallMode.Glass)
                node.Shape.AddTo(mesh);
            if (detailLevel == 0)
            {
                node.Wall = mesh;
                NodeRebuilds++;
            }

            return mesh;
        }

        internal static void Update(PlanetFactory factory, Camera camera)
        {
            FlushChanges();
            PrewarmStructureMeshes(factory, camera);
        }

        internal static int Draw(PlanetFactory factory, Camera camera, int itemId = 0)
        {
            if (factory?.cargoTraffic == null || camera == null)
            {
                return 0;
            }

            var model = factory.planet?.factoryModel;
            if (model == null || model.disableFactoryEntities ||
                model.gpuiManager == null || !model.gpuiManager.renderEntity)
            {
                return 0;
            }

            Initialize(factory);
            FlushChanges();
            OrbitalRenderBounds.Prepare(camera);
            int drawn = 0;
            foreach (var entry in RenderChunks(camera))
            {
                if (itemId > 0 && entry.Key.ItemId != itemId)
                {
                    continue;
                }

                var definition = GetDefinition(entry.Key.ItemId);
                var dimensions = definition.Geometry;
                Vector3 origin = Origin(entry.Key);
                var chunk = entry.Value;
                float span = entry.Key.Distant ? DistantChunkSize : StructureChunkSize;
                var bounds = new Bounds(origin + Vector3.one * span * 0.5f,
                    Vector3.one * (span + 8 + dimensions.CenterHeight + dimensions.Radius * 2));
                if (!chunk.Dirty && chunk.HasBounds)
                {
                    bounds = chunk.Bounds;
                    bounds.center += origin;
                }

                if (!OrbitalRenderBounds.IsVisible(camera, bounds, factory.planet.realRadius - 2))
                {
                    continue;
                }

                int level = entry.Key.Distant ? 2 : StructureDetailLevel(camera, bounds, dimensions, definition);
                if (NeedsDetailBuild(chunk, level))
                {
                    RequestDetailBuild(factory, entry.Key, chunk, level, true);
                }

                if (!chunk.DetailMeshes.ContainsKey(level))
                {
                    if (chunk.DetailMeshes.ContainsKey(1))
                    {
                        level = 1;
                    }
                    else if (chunk.DetailMeshes.ContainsKey(0))
                    {
                        level = 0;
                    }
                    else if (chunk.DetailMeshes.ContainsKey(2))
                    {
                        level = 2;
                    }
                    else
                    {
                        continue;
                    }
                }

                var meshes = GetDetailMeshes(factory, entry.Key, chunk, level);
                if (meshes.Wall == null && meshes.Core == null)
                {
                    continue;
                }

                var surfaces = TrackMaterialLibrary.Get(entry.Key.ItemId);
                Matrix4x4 transform = Matrix4x4.Translate(origin);
                if (meshes.Wall != null)
                {
                    if (surfaces.WallMode == TrackWallMode.Glass)
                    {
                        DrawSurface(factory, camera, entry.Key, chunk, surfaces, meshes.Wall, transform,
                            bounds.center, 2, TrackSurfaceRole.InnerWall);
                    }

                    DrawSurface(factory, camera, entry.Key, chunk, surfaces, meshes.Wall, transform,
                        bounds.center, 0, TrackSurfaceRole.Wall);
                    DrawSurface(factory, camera, entry.Key, chunk, surfaces, meshes.Wall, transform,
                        bounds.center, 1, TrackSurfaceRole.Fittings);
                }

                if (meshes.Core != null)
                {
                    DrawSurface(factory, camera, entry.Key, chunk, surfaces, meshes.Core, transform,
                        bounds.center, 0, TrackSurfaceRole.Core);
                    DrawSurface(factory, camera, entry.Key, chunk, surfaces, meshes.Core, transform,
                        bounds.center, 1, TrackSurfaceRole.Fittings);
                    DrawSurface(factory, camera, entry.Key, chunk, surfaces, meshes.Core, transform,
                        bounds.center, 4, TrackSurfaceRole.Detail);
                }

                drawn++;
            }

            DrawStructureShadows(factory, camera, itemId);
            return drawn;
        }

        private static void DrawSurface(PlanetFactory factory, Camera camera, ChunkKey key, Chunk chunk,
            TrackMaterialSet surfaces, Mesh mesh, Matrix4x4 transform, Vector3 radial,
            int submesh, TrackSurfaceRole role)
        {
            if (submesh >= mesh.subMeshCount || mesh.GetIndexCount(submesh) == 0)
            {
                return;
            }

            Material material = surfaces.Get(role);
            if (material == null)
            {
                return;
            }

            var definition = GetDefinition(key.ItemId);
            var properties = TrackLighting.SteelLighting(radial);
            if (definition.Appearance.ApplyEffects != null)
            {
                var effectKey = new EffectKey(key.ItemId, key.Group, role);
                if (!effectStates.TryGetValue(effectKey, out var state))
                {
                    effectStates.Add(effectKey, state = new EffectState());
                }

                if (state.Frame != Time.frameCount)
                {
                    state.Properties.Clear();
                    TrackLighting.Apply(state.Properties, radial);
                    state.Applied = TrackMaterialLibrary.Effects(key.ItemId,
                        new TrackEffectContext(factory, chunk.Representative, key.Group, role), state.Properties);
                    state.Frame = Time.frameCount;
                }

                if (state.Applied)
                {
                    properties = state.Properties;
                }
            }

            Graphics.DrawMesh(mesh, transform, material, 0, camera, submesh, properties, ShadowCastingMode.Off,
                definition.Appearance.ReceiveShadows);
        }

        private static void FreeMeshes()
        {
            ClearDistantChunks();
            foreach (var chunk in chunks.Values)
            {
                FreeDetailMeshes(chunk);
            }

            chunks.Clear();
            effectStates.Clear();
            keys.Clear();
            geometry.Clear();
            current = null;
            TrackLighting.Reset();
        }

        internal static void Reset()
        {
            FreeMeshes();
            lock (changesLock)
            {
                changes.Clear();
            }

            changedNodes.Clear();
            neighbors.Clear();
        }

        internal static void Dispose()
        {
            Reset();
            TrackMaterialLibrary.Dispose();
            TrackModelAssets.Dispose();
            TrackLighting.Dispose();
            definitions.Clear();
        }
    }
}
