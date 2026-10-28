using HarmonyLib;
using ProjectOrbitalRing.Utils;
using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    [HarmonyPatch]
    internal static class OrbitalConstructionOrientation
    {
        private static bool IsComponent(int itemId)
        {
            bool isTrackItem = itemId == ProtoID.I轨道连接组件 || itemId == ProtoID.I星环电网组件
                || itemId == ProtoID.I粒子加速轨道;
            return isTrackItem && TrackRenderer.IsRegistered(itemId);
        }

        private static bool HasNeighbors(PlanetFactory factory, int entityId)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                factory.ReadObjectConn(entityId, slot, out _, out int neighbor, out _);
                if (neighbor != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static Quaternion RingRotation(Vector3 position, Quaternion rotation)
        {
            Vector3 up = position.normalized;
            Vector3 tangent = Vector3.Cross(Vector3.up, up).normalized;
            if (Vector3.Dot(rotation * Vector3.forward, tangent) < 0)
            {
                tangent = -tangent;
            }

            return tangent.sqrMagnitude > 0.000001f ? Quaternion.LookRotation(tangent, up) : rotation;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddPrebuildDataWithComponents))]
        private static void AlignPrebuild(ref PrebuildData prebuild)
        {
            if (IsComponent(prebuild.protoId))
            {
                prebuild.rot = RingRotation(prebuild.pos, prebuild.rot);
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(PlanetFactory), nameof(PlanetFactory.AddEntityDataWithComponents))]
        private static void AlignEntity(ref EntityData entity)
        {
            if (IsComponent(entity.protoId))
            {
                entity.rot = RingRotation(entity.pos, entity.rot);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.AlterBeltRenderer))]
        private static void AlignNativeIsolated(CargoTraffic __instance, int beltId)
        {
            if (beltId > 0 && beltId < __instance.beltPool.Length)
            {
                AlignIsolated(__instance.factory, __instance.beltPool[beltId].entityId, false);
            }
        }

        internal static void AlignIsolated(PlanetFactory factory, int entityId, bool refresh = true)
        {
            if (entityId <= 0 || entityId >= factory.entityPool.Length)
            {
                return;
            }

            ref var entity = ref factory.entityPool[entityId];
            if (!IsComponent(entity.protoId) || HasNeighbors(factory, entityId))
            {
                return;
            }

            Quaternion rotation = RingRotation(entity.pos, entity.rot);
            if (Quaternion.Angle(entity.rot, rotation) < 0.01f)
            {
                return;
            }

            entity.rot = rotation;
            if (refresh)
            {
                factory.cargoTraffic.AlterBeltRenderer(
                    entity.beltId, factory.entityPool, factory.planet.physics?.colChunks);
            }
            else if (entity.colliderId > 0 && factory.planet.physics?.colChunks != null)
            {
                int chunk = entity.colliderId >> 20;
                int index = entity.colliderId & 0xFFFFF;
                factory.planet.physics.colChunks[chunk].colliderPool[index].q = rotation;
                factory.planet.physics.SetPlanetPhysicsColliderDirty();
            }
        }
    }
}
