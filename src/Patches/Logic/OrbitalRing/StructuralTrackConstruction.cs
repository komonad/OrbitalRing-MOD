using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ProjectOrbitalRing.Patches.Logic.OrbitalRing
{
    [HarmonyPatch]
    internal static class StructuralTrackConstruction
    {
        private sealed class FactoryState
        {
            public FactoryState()
            {
            }

            internal bool Initialized;
            internal bool Initializing;
            internal bool Success;
        }

        private sealed class Node
        {
            internal int ObjectId;
            internal int BeltId;
            internal int Previous;
            internal int Next;
            internal readonly List<int> Neighbors = new List<int>(2);
        }

        private readonly struct PreviewLink
        {
            internal readonly BuildPreview Preview;
            internal readonly BuildPreview Other;
            internal readonly int OtherObject;

            internal PreviewLink(BuildPreview preview, BuildPreview other, int otherObject)
            {
                Preview = preview;
                Other = other;
                OtherObject = otherObject;
            }
        }

        private struct PreviewPorts
        {
            internal BuildPreview PreviousPreview;
            internal int PreviousObject;
            internal BuildPreview NextPreview;
            internal int NextObject;
        }

        private static readonly ConditionalWeakTable<PlanetFactory, FactoryState> factories =
            new ConditionalWeakTable<PlanetFactory, FactoryState>();
        private static readonly List<PreviewLink> previewLinks = new List<PreviewLink>();
        private static readonly List<BuildPreview> blueprintPreviews = new List<BuildPreview>();
        private static readonly Dictionary<BuildPreview, PreviewPorts> previewPorts =
            new Dictionary<BuildPreview, PreviewPorts>();
        private static int normalizationDepth;

        internal static void Update()
        {
            if (StructuralTrackPolicy.Enabled && GameMain.isRunning)
            {
                EnsureFactory(GameMain.localPlanet?.factory);
            }
        }

        private static bool EnsureFactory(PlanetFactory factory)
        {
            if (factory?.cargoTraffic == null || !StructuralTrackPolicy.Enabled)
            {
                return false;
            }
            FactoryState state = factories.GetOrCreateValue(factory);
            return state.Initialized ? state.Success : NormalizeFactory(factory);
        }

        internal static bool NormalizeFactory(PlanetFactory factory)
        {
            if (!StructuralTrackPolicy.Enabled || factory?.cargoTraffic?.beltPool == null)
            {
                return false;
            }
            FactoryState state = factories.GetOrCreateValue(factory);
            if (state.Initializing)
            {
                return false;
            }
            state.Initializing = true;
            state.Success = true;
            try
            {
                var visited = new HashSet<int>();
                for (int entityId = 1; entityId < factory.entityCursor; entityId++)
                {
                    if (StructuralTrackPolicy.IsTrackObject(factory, entityId) && !visited.Contains(entityId))
                    {
                        state.Success &= NormalizeNodes(factory, CollectNodes(factory, entityId, visited));
                    }
                }
                for (int prebuildId = 1; prebuildId < factory.prebuildCursor; prebuildId++)
                {
                    int objectId = -prebuildId;
                    if (StructuralTrackPolicy.IsTrackObject(factory, objectId) && !visited.Contains(objectId))
                    {
                        state.Success &= NormalizeNodes(factory, CollectNodes(factory, objectId, visited));
                    }
                }
                state.Initialized = true;
                return state.Success;
            }
            finally
            {
                state.Initializing = false;
            }
        }

        internal static bool NormalizeComponent(PlanetFactory factory, int entityId)
        {
            return StructuralTrackPolicy.Enabled && StructuralTrackPolicy.IsTrackObject(factory, entityId) &&
                NormalizeNodes(factory, CollectNodes(factory, entityId, new HashSet<int>()));
        }

        private static List<Node> CollectNodes(PlanetFactory factory, int first, HashSet<int> visited)
        {
            var nodes = new List<Node>();
            var pending = new Queue<int>();
            visited.Add(first);
            pending.Enqueue(first);
            while (pending.Count > 0)
            {
                int objectId = pending.Dequeue();
                var node = new Node
                {
                    ObjectId = objectId,
                    BeltId = objectId > 0 ? factory.entityPool[objectId].beltId : 0,
                };
                for (int slot = 0; slot < 4; slot++)
                {
                    factory.ReadObjectConn(objectId, slot, out _, out int other, out _);
                    if (other == objectId || !StructuralTrackPolicy.IsTrackObject(factory, other) ||
                        !StructuralTrackPolicy.CanConnect(factory, objectId, other) || node.Neighbors.Contains(other))
                    {
                        continue;
                    }
                    node.Neighbors.Add(other);
                    if (visited.Add(other))
                    {
                        pending.Enqueue(other);
                    }
                }
                nodes.Add(node);
            }
            return nodes;
        }

        private static bool NormalizeNodes(PlanetFactory factory, List<Node> nodes)
        {
            try
            {
                return NormalizeNodesCore(factory, nodes);
            }
            catch (Exception error)
            {
                global::ProjectOrbitalRing.ProjectOrbitalRing.logger?.LogError(
                    "Orbital track normalization failed: planet=" + factory.planetId +
                    ", node=" + nodes[0].ObjectId + "\n" + error);
                return false;
            }
        }

        private static bool NormalizeNodesCore(PlanetFactory factory, List<Node> nodes)
        {
            var entityIds = new List<int>(nodes.Count);
            bool assignable = true;
            bool changed = false;
            foreach (Node node in nodes)
            {
                if (node.ObjectId > 0)
                {
                    entityIds.Add(node.ObjectId);
                }
                assignable &= AssignNeighbors(factory, node);
                changed |= !IsCanonical(factory, node.ObjectId);
            }
            if (!StructuralTrackCargo.CanNormalize(factory, entityIds))
            {
                return false;
            }
            normalizationDepth++;
            try
            {
                foreach (Node node in nodes)
                {
                    changed |= DisconnectExternal(factory, node.ObjectId);
                }
                if (!assignable)
                {
                    foreach (Node node in nodes)
                    {
                        if (node.BeltId > 0)
                        {
                            BeltComponent belt = factory.cargoTraffic.beltPool[node.BeltId];
                            factory.cargoTraffic.AlterBeltConnections(node.BeltId, belt.outputId,
                                belt.rightInputId, belt.backInputId, belt.leftInputId);
                        }
                    }
                    global::ProjectOrbitalRing.ProjectOrbitalRing.logger?.LogWarning(
                        "Orbital track component has multiple neighbors in one longitude direction: planet=" +
                        factory.planetId + ", node=" + nodes[0].ObjectId);
                    return false;
                }
                if (!changed)
                {
                    return true;
                }

                // Native path segments and reciprocal object ports are rebuilt from the same adjacency snapshot.
                foreach (Node node in nodes)
                {
                    if (node.BeltId > 0)
                    {
                        factory.cargoTraffic.AlterBeltConnections(node.BeltId, 0, 0, 0, 0);
                    }
                }
                foreach (Node node in nodes)
                {
                    for (int slot = 0; slot < 4; slot++)
                    {
                        factory.ClearObjectConn(node.ObjectId, slot);
                    }
                    AlignObject(factory, node.ObjectId);
                }
                foreach (Node node in nodes)
                {
                    if (node.Next != 0)
                    {
                        factory.WriteObjectConn(node.ObjectId, 0, true, node.Next, 1);
                    }
                }
                foreach (Node node in nodes)
                {
                    if (node.BeltId > 0)
                    {
                        int nextBelt = node.Next > 0 ? factory.entityPool[node.Next].beltId : 0;
                        int previousBelt = node.Previous > 0 ? factory.entityPool[node.Previous].beltId : 0;
                        factory.cargoTraffic.AlterBeltConnections(node.BeltId, nextBelt, 0, previousBelt, 0);
                    }
                }
            }
            finally
            {
                normalizationDepth--;
            }
            if (entityIds.Count > 0)
            {
                OrbitalConnect.OnBeltBuiltPostPatch(ref factory, entityIds[0]);
            }
            return true;
        }

        private static bool AssignNeighbors(PlanetFactory factory, Node node)
        {
            node.Previous = 0;
            node.Next = 0;
            foreach (int other in node.Neighbors)
            {
                float delta = LongitudeDelta(Position(factory, node.ObjectId), Position(factory, other));
                if (Mathf.Abs(delta) < 0.00001f)
                {
                    return false;
                }
                if (delta > 0)
                {
                    if (node.Next != 0)
                    {
                        return false;
                    }
                    node.Next = other;
                }
                else
                {
                    if (node.Previous != 0)
                    {
                        return false;
                    }
                    node.Previous = other;
                }
            }
            return true;
        }

        private static bool IsCanonical(PlanetFactory factory, int objectId)
        {
            int previous = 0;
            int next = 0;
            for (int slot = 0; slot < 16; slot++)
            {
                factory.ReadObjectConn(objectId, slot, out bool output, out int other, out int otherSlot);
                if (other == 0)
                {
                    continue;
                }
                if (slot > 1 || !StructuralTrackPolicy.IsTrackObject(factory, other) ||
                    !StructuralTrackPolicy.CanConnect(factory, objectId, other))
                {
                    return false;
                }
                bool forward = LongitudeDelta(Position(factory, objectId), Position(factory, other)) > 0;
                if (output != forward || slot != (forward ? 0 : 1) || otherSlot != (forward ? 1 : 0))
                {
                    return false;
                }
                if (forward)
                {
                    next = other;
                }
                else
                {
                    previous = other;
                }
            }
            if (objectId < 0)
            {
                return true;
            }
            int beltId = factory.entityPool[objectId].beltId;
            if (!StructuralTrackPolicy.IsTrackBelt(factory.cargoTraffic, beltId))
            {
                return false;
            }
            BeltComponent belt = factory.cargoTraffic.beltPool[beltId];
            int nextBelt = next > 0 ? factory.entityPool[next].beltId : 0;
            int previousBelt = previous > 0 ? factory.entityPool[previous].beltId : 0;
            int inputCount = (belt.rightInputId == 0 ? 0 : 1) + (belt.backInputId == 0 ? 0 : 1);
            inputCount += belt.leftInputId == 0 ? 0 : 1;
            // Native path geometry owns rotation and classifies the sole input into any of its three slots.
            return belt.outputId == nextBelt && belt.mainInputId == previousBelt &&
                inputCount == (previousBelt == 0 ? 0 : 1);
        }

        private static bool DisconnectExternal(PlanetFactory factory, int objectId)
        {
            bool changed = false;
            for (int slot = 0; slot < 16; slot++)
            {
                factory.ReadObjectConn(objectId, slot, out _, out int other, out int otherSlot);
                if (other == 0 || (slot < 4 && other != objectId &&
                    StructuralTrackPolicy.IsTrackObject(factory, other) &&
                    StructuralTrackPolicy.CanConnect(factory, objectId, other)))
                {
                    continue;
                }
                if (objectId > 0 && other > 0)
                {
                    factory.ApplyEntityDisconnection(other, objectId, otherSlot, slot);
                }
                factory.ClearObjectConn(objectId, slot);
                changed = true;
            }
            return changed;
        }

        private static Vector3 Position(PlanetFactory factory, int objectId) => objectId > 0
            ? factory.entityPool[objectId].pos : factory.prebuildPool[-objectId].pos;

        private static Quaternion Rotation(Vector3 position)
        {
            Vector3 up = position.normalized;
            Vector3 tangent = Vector3.Cross(Vector3.up, up).normalized;
            return Quaternion.LookRotation(tangent, up);
        }

        private static float LongitudeDelta(Vector3 first, Vector3 last)
        {
            return Mathf.DeltaAngle(Mathf.Atan2(first.x, first.z) * Mathf.Rad2Deg,
                Mathf.Atan2(last.x, last.z) * Mathf.Rad2Deg);
        }

        private static void AlignObject(PlanetFactory factory, int objectId)
        {
            Quaternion rotation = Rotation(Position(factory, objectId));
            if (objectId > 0)
            {
                factory.entityPool[objectId].rot = rotation;
                factory.entityPool[objectId].tilt = 0;
            }
            else
            {
                factory.prebuildPool[-objectId].rot = rotation;
                factory.prebuildPool[-objectId].rot2 = rotation;
                factory.prebuildPool[-objectId].tilt = 0;
                if (factory.planet.physics != null)
                {
                    factory.PostRefreshPrebuildDisplay(-objectId);
                }
            }
        }

        internal static void NormalizePreviews(PlanetFactory factory, IList<BuildPreview> previews)
        {
            NormalizePreviewGraph(factory, previews, false);
        }

        private static void NormalizePreviewGraph(PlanetFactory factory, IList<BuildPreview> previews,
            bool reciprocalPorts)
        {
            if (!StructuralTrackPolicy.Enabled || factory == null || previews == null)
            {
                return;
            }
            previewLinks.Clear();
            previewPorts.Clear();
            foreach (BuildPreview preview in previews)
            {
                if (!StructuralTrackPolicy.IsTrackItem(preview.item?.ID ?? 0))
                {
                    continue;
                }
                previewLinks.Add(new PreviewLink(preview, preview.input, preview.inputObjId));
                previewLinks.Add(new PreviewLink(preview, preview.output, preview.outputObjId));
                preview.input = null;
                preview.output = null;
                preview.inputObjId = 0;
                preview.outputObjId = 0;
                preview.inputToSlot = 1;
                preview.inputFromSlot = 0;
                preview.outputFromSlot = 0;
                preview.outputToSlot = 1;
                preview.inputOffset = 0;
                preview.outputOffset = 0;
                preview.tilt = 0;
                preview.lrot = Rotation(preview.lpos);
                preview.lrot2 = preview.lrot;
            }
            foreach (PreviewLink link in previewLinks)
            {
                ConnectPreview(factory, link);
            }
            if (reciprocalPorts)
            {
                // Native blueprint validation discards belt inputs and retains the corresponding outputs.
                foreach (var entry in previewPorts)
                {
                    BuildPreview preview = entry.Key;
                    if (preview.condition == EBuildCondition.InputConflict ||
                        preview.condition == EBuildCondition.Occupied)
                    {
                        continue;
                    }
                    PreviewPorts ports = entry.Value;
                    preview.input = ports.PreviousPreview;
                    preview.inputObjId = ports.PreviousPreview == null ? ports.PreviousObject : 0;
                    preview.output = ports.NextPreview;
                    preview.outputObjId = ports.NextPreview == null ? ports.NextObject : 0;
                }
            }
            previewLinks.Clear();
            previewPorts.Clear();
            foreach (BuildPreview preview in previews)
            {
                if (!PreviewLinkAllowed(factory, preview, preview.input, preview.inputObjId) ||
                    !PreviewLinkAllowed(factory, preview, preview.output, preview.outputObjId))
                {
                    preview.condition = EBuildCondition.InputConflict;
                }
            }
        }

        internal static void NormalizePathPreviews(BuildTool_Path tool)
        {
            NormalizePreviews(tool.factory, tool.buildPreviews);
        }

        private static bool PreviewLinkAllowed(PlanetFactory factory, BuildPreview preview,
            BuildPreview other, int otherObject)
        {
            if (preview.item == null)
            {
                return true;
            }
            if (other?.item != null)
            {
                return StructuralTrackPolicy.CanConnectPreview(factory, preview.item.ID, preview.lpos,
                    other.item.ID, other.lpos);
            }
            return otherObject == 0 || !StructuralTrackPolicy.TryReadObject(factory, otherObject,
                out int otherItem, out Vector3 otherPosition) ||
                StructuralTrackPolicy.CanConnectPreview(factory, preview.item.ID, preview.lpos,
                    otherItem, otherPosition);
        }

        private static void ConnectPreview(PlanetFactory factory, PreviewLink link)
        {
            BuildPreview preview = link.Preview;
            BuildPreview other = link.Other;
            int otherObject = link.OtherObject;
            if (other == null && otherObject == 0)
            {
                return;
            }
            int otherItem;
            Vector3 otherPosition;
            if (other != null)
            {
                otherItem = other.item?.ID ?? 0;
                otherPosition = other.lpos;
            }
            else if (!StructuralTrackPolicy.TryReadObject(factory, otherObject, out otherItem, out otherPosition))
            {
                preview.condition = EBuildCondition.InputConflict;
                return;
            }
            if (!StructuralTrackPolicy.CanConnectPreview(factory, preview.item.ID, preview.lpos,
                otherItem, otherPosition))
            {
                preview.condition = EBuildCondition.InputConflict;
                return;
            }
            float delta = LongitudeDelta(preview.lpos, otherPosition);
            if (Mathf.Abs(delta) < 0.00001f)
            {
                return;
            }
            bool forward = delta > 0;
            int targetObject = other?.coverObjId ?? otherObject;
            if (preview.coverObjId != 0 && !CanUseSlot(factory, preview.coverObjId, targetObject, forward ? 0 : 1))
            {
                preview.condition = EBuildCondition.Occupied;
                return;
            }
            if (otherObject != 0 && !CanUseSlot(factory, otherObject, preview.coverObjId, forward ? 1 : 0))
            {
                preview.condition = EBuildCondition.Occupied;
                return;
            }
            if (!ReservePreviewPort(preview, other, otherObject, forward) ||
                (other != null && !ReservePreviewPort(other, preview, preview.coverObjId, !forward)))
            {
                preview.condition = EBuildCondition.InputConflict;
                return;
            }
            if (forward)
            {
                if ((preview.output != null && preview.output != other) ||
                    (preview.outputObjId != 0 && preview.outputObjId != otherObject))
                {
                    preview.condition = EBuildCondition.InputConflict;
                    return;
                }
                preview.output = other;
                preview.outputObjId = otherObject;
            }
            else
            {
                if ((preview.input != null && preview.input != other) ||
                    (preview.inputObjId != 0 && preview.inputObjId != otherObject))
                {
                    preview.condition = EBuildCondition.InputConflict;
                    return;
                }
                preview.input = other;
                preview.inputObjId = otherObject;
            }
        }

        private static bool ReservePreviewPort(BuildPreview preview, BuildPreview other, int objectId, bool forward)
        {
            if (other != null && other.coverObjId != 0)
            {
                objectId = other.coverObjId;
            }
            previewPorts.TryGetValue(preview, out PreviewPorts ports);
            BuildPreview occupiedPreview = forward ? ports.NextPreview : ports.PreviousPreview;
            int occupiedObject = forward ? ports.NextObject : ports.PreviousObject;
            bool sameTarget = occupiedObject != 0 && objectId != 0 ? occupiedObject == objectId
                : occupiedPreview == other && occupiedObject == objectId;
            if ((occupiedPreview != null || occupiedObject != 0) && !sameTarget)
            {
                preview.condition = EBuildCondition.InputConflict;
                return false;
            }
            if (forward)
            {
                ports.NextPreview = other;
                ports.NextObject = objectId;
            }
            else
            {
                ports.PreviousPreview = other;
                ports.PreviousObject = objectId;
            }
            previewPorts[preview] = ports;
            return true;
        }

        private static bool CanUseSlot(PlanetFactory factory, int objectId, int other, int slot)
        {
            int neighbors = 0;
            bool containsOther = false;
            for (int index = 0; index < 4; index++)
            {
                factory.ReadObjectConn(objectId, index, out _, out int neighbor, out _);
                if (!StructuralTrackPolicy.IsTrackObject(factory, neighbor))
                {
                    continue;
                }
                neighbors++;
                containsOther |= neighbor == other || ReplacesPrebuild(factory, neighbor, other);
            }
            if (neighbors >= 2 && !containsOther)
            {
                return false;
            }
            factory.ReadObjectConn(objectId, slot, out _, out int occupied, out _);
            if (occupied == 0 || (other != 0 && occupied == other))
            {
                return true;
            }
            // Drone completion replaces a prebuild reference with the entity at the same placement.
            return ReplacesPrebuild(factory, occupied, other);
        }

        private static bool ReplacesPrebuild(PlanetFactory factory, int prebuild, int entity)
        {
            return prebuild < 0 && entity > 0 && StructuralTrackPolicy.IsTrackObject(factory, prebuild) &&
                StructuralTrackPolicy.IsTrackObject(factory, entity) &&
                factory.prebuildPool[-prebuild].protoId == factory.entityPool[entity].protoId &&
                (Position(factory, prebuild) - Position(factory, entity)).sqrMagnitude < 0.000001f;
        }

        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.WriteObjectConn))]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.WriteObjectConnDirect))]
        private static bool CanonicalConnection(PlanetFactory __instance, int objId, ref int slot,
            ref bool isOutput, int otherObjId, ref int otherSlot)
        {
            if (normalizationDepth > 0 || !StructuralTrackPolicy.Enabled ||
                !StructuralTrackPolicy.IsTrackObject(__instance, objId) ||
                !StructuralTrackPolicy.IsTrackObject(__instance, otherObjId))
            {
                return true;
            }
            if (!StructuralTrackPolicy.CanConnect(__instance, objId, otherObjId) || objId == otherObjId)
            {
                return false;
            }
            float delta = LongitudeDelta(Position(__instance, objId), Position(__instance, otherObjId));
            if (Mathf.Abs(delta) < 0.00001f)
            {
                return false;
            }
            isOutput = delta > 0;
            slot = isOutput ? 0 : 1;
            otherSlot = isOutput ? 1 : 0;
            return CanUseSlot(__instance, objId, otherObjId, slot) &&
                CanUseSlot(__instance, otherObjId, objId, otherSlot);
        }

        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddPrebuildDataWithComponents))]
        private static void AlignPrebuild(ref PrebuildData prebuild)
        {
            if (StructuralTrackPolicy.Enabled && StructuralTrackPolicy.IsTrackItem(prebuild.protoId))
            {
                prebuild.rot = Rotation(prebuild.pos);
                prebuild.rot2 = prebuild.rot;
                prebuild.tilt = 0;
            }
        }

        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddEntityDataWithComponents))]
        private static void AlignEntity(ref EntityData entity)
        {
            if (StructuralTrackPolicy.Enabled && StructuralTrackPolicy.IsTrackItem(entity.protoId))
            {
                entity.rot = Rotation(entity.pos);
                entity.tilt = 0;
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.DeterminePreviews))]
        private static void PreparePathFactory(BuildTool_Path __instance)
        {
            if (StructuralTrackPolicy.Enabled && StructuralTrackPolicy.IsTrackItem(__instance.handItem?.ID ?? 0))
            {
                EnsureFactory(__instance.factory);
                NormalizeEndpoint(__instance.factory, __instance.startObjectId);
                NormalizeEndpoint(__instance.factory, __instance.castObjectId);
            }
        }

        private static void NormalizeEndpoint(PlanetFactory factory, int objectId)
        {
            if (StructuralTrackPolicy.IsTrackObject(factory, objectId) && !IsCanonical(factory, objectId))
            {
                NormalizeComponent(factory, objectId);
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.CheckBuildConditions))]
        [HarmonyPatch(typeof(BuildTool_Path), nameof(BuildTool_Path.CreatePrebuilds))]
        private static void PreparePath(BuildTool_Path __instance) =>
            NormalizePreviews(__instance.factory, __instance.buildPreviews);

        [HarmonyPrefix, HarmonyPatch(typeof(BuildTool_BlueprintPaste), "CheckBuildConditions")]
        [HarmonyPatch(typeof(BuildTool_BlueprintPaste), "CreatePrebuilds")]
        private static void PrepareBlueprint(BuildTool_BlueprintPaste __instance)
        {
            NormalizeBlueprint(__instance);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(BuildTool_BlueprintPaste), "DeterminePreviews")]
        private static void AlignBlueprint(BuildTool_BlueprintPaste __instance)
        {
            NormalizeBlueprint(__instance);
        }

        private static void NormalizeBlueprint(BuildTool_BlueprintPaste tool)
        {
            if (!StructuralTrackPolicy.Enabled || tool.bpPool == null)
            {
                return;
            }
            EnsureFactory(tool.factory);
            blueprintPreviews.Clear();
            int count = Math.Min(tool.bpCursor, tool.bpPool.Length);
            for (int index = 0; index < count; index++)
            {
                if (tool.bpPool[index] != null)
                {
                    blueprintPreviews.Add(tool.bpPool[index]);
                }
            }
            try
            {
                NormalizePreviewGraph(tool.factory, blueprintPreviews, true);
            }
            finally
            {
                blueprintPreviews.Clear();
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.OnBeltBuilt))]
        private static void Built(PlanetFactory __instance, int beltEntityId)
        {
            if (StructuralTrackPolicy.Enabled && normalizationDepth == 0 &&
                StructuralTrackPolicy.IsTrackObject(__instance, beltEntityId))
            {
                EnsureFactory(__instance);
                if (!IsCanonical(__instance, beltEntityId))
                {
                    NormalizeComponent(__instance, beltEntityId);
                }
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.Import))]
        private static void Imported(CargoTraffic __instance)
        {
            if (__instance.factory != null)
            {
                factories.Remove(__instance.factory);
            }
        }
    }
}
