using System.Collections.Generic;
using HarmonyLib;

namespace ProjectOrbitalRing.Rendering
{
    [HarmonyPatch]
    internal static class TrackRenderPatches
    {
        private sealed class RemovedNode
        {
            internal int EntityId;
            internal readonly List<int> Neighbors = new List<int>(4);
        }

        private static bool IsTrackBelt(CargoTraffic traffic, int beltId)
        {
            if (traffic?.beltPool == null || beltId <= 0 || beltId >= traffic.beltPool.Length ||
                traffic.beltPool[beltId].id != beltId)
            {
                return false;
            }

            int entityId = traffic.beltPool[beltId].entityId;
            return entityId > 0 && entityId < traffic.factory.entityPool.Length &&
                traffic.factory.entityPool[entityId].id == entityId &&
                TrackRenderer.IsRegistered(traffic.factory.entityPool[entityId].protoId);
        }

        internal static bool IsTrackPath(CargoTraffic traffic, int pathId)
        {
            var path = traffic.GetCargoPath(pathId);
            if (path == null || path.belts.Count == 0)
            {
                return false;
            }

            foreach (int beltId in path.belts)
            {
                if (!IsTrackBelt(traffic, beltId))
                {
                    return false;
                }
            }

            return true;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.SetBeltState))]
        private static bool SetState(CargoTraffic __instance, int beltId) => !IsTrackBelt(__instance, beltId);

        [HarmonyPostfix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.AlterBeltRenderer))]
        private static void BeltVisual(CargoTraffic __instance, int beltId)
        {
            if (!IsTrackBelt(__instance, beltId))
            {
                return;
            }

            // Native geometry generation also owns collision updates and runs before mesh removal.
            __instance.RemoveBeltRenderer(beltId);
            TrackRenderer.Dirty(__instance.factory, __instance.beltPool[beltId].entityId);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.AlterPathRenderer))]
        private static bool PathVisual(CargoTraffic __instance, int pathId)
        {
            if (!IsTrackPath(__instance, pathId))
            {
                return true;
            }

            __instance.RemovePathRenderer(pathId);
            return false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.NewBeltComponent))]
        private static void Added(CargoTraffic __instance, int entityId)
        {
            if (entityId > 0 && entityId < __instance.factory.entityPool.Length &&
                TrackRenderer.IsRegistered(__instance.factory.entityPool[entityId].protoId))
            {
                TrackRenderer.Dirty(__instance.factory, entityId);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.CreateEntityLogicComponents))]
        private static void Created(PlanetFactory __instance, int entityId)
        {
            if (TrackTopology.IsNode(__instance, entityId))
            {
                TrackRenderer.Dirty(__instance, entityId);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.Import))]
        private static void Imported(CargoTraffic __instance)
        {
            for (int beltId = 1; beltId < __instance.beltCursor; beltId++)
            {
                if (IsTrackBelt(__instance, beltId))
                {
                    TrackRenderer.Dirty(__instance.factory, __instance.beltPool[beltId].entityId);
                }
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.RemoveBeltComponent))]
        private static void Removing(CargoTraffic __instance, int id, out RemovedNode __state)
        {
            __state = null;
            if (!IsTrackBelt(__instance, id))
            {
                return;
            }

            __state = new RemovedNode
            {
                EntityId = __instance.beltPool[id].entityId
            };
            TrackTopology.CopyNeighbors(__instance.factory, __state.EntityId, __state.Neighbors);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.RemoveBeltComponent))]
        private static void Removed(CargoTraffic __instance, RemovedNode __state)
        {
            if (__state == null)
            {
                return;
            }

            TrackRenderer.Dirty(__instance.factory, __state.EntityId);
            foreach (int neighbor in __state.Neighbors)
            {
                TrackRenderer.Dirty(__instance.factory, neighbor);
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.WriteObjectConn))]
        [HarmonyPatch(typeof(PlanetFactory), "WriteObjectConnDirect")]
        private static void BeforeLink(PlanetFactory __instance, int objId, int slot, out int __state)
        {
            __state = 0;
            if (objId > 0 && objId < __instance.entityPool.Length && slot >= 0 && slot < 4)
            {
                __instance.ReadObjectConn(objId, slot, out _, out __state, out _);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.WriteObjectConn))]
        [HarmonyPatch(typeof(PlanetFactory), "WriteObjectConnDirect")]
        private static void Linked(PlanetFactory __instance, int objId, int otherObjId, int __state)
        {
            if (TrackTopology.IsNode(__instance, objId) || TrackTopology.IsNode(__instance, otherObjId) ||
                TrackTopology.IsNode(__instance, __state))
            {
                TrackRenderer.Dirty(__instance, objId);
                TrackRenderer.Dirty(__instance, otherObjId);
                TrackRenderer.Dirty(__instance, __state);
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.UpgradeEntityWithComponents))]
        private static void BeforeUpgrade(PlanetFactory __instance, int entityId, out bool __state) =>
            __state = TrackTopology.IsNode(__instance, entityId);

        [HarmonyPostfix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.UpgradeEntityWithComponents))]
        private static void Upgraded(PlanetFactory __instance, int entityId, bool __state)
        {
            if (__state || TrackTopology.IsNode(__instance, entityId))
            {
                TrackRenderer.Dirty(__instance, entityId);
            }
        }
    }
}
