using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ProjectOrbitalRing.Patches.Logic.OrbitalRing;
using ProjectOrbitalRing.Utils;
using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalColliderActivity
    {
        private sealed class ControllerState
        {
            internal int EntityId;
            internal int AssemblerId;
            internal float NextProbe;
        }

        private static ConditionalWeakTable<PlanetFactory, Dictionary<int, ControllerState>> controllers =
            new ConditionalWeakTable<PlanetFactory, Dictionary<int, ControllerState>>();

        internal static bool IsWorking(PlanetFactory factory, int ringIndex)
        {
            if (factory == null || factory.planet == null || factory.factorySystem == null)
            {
                return false;
            }

            var data = OrbitalStationManager.Instance.GetPlanetOrbitalRingData(factory.planet.id);
            if (data == null || ringIndex < 0 || ringIndex >= data.Rings.Count)
            {
                return false;
            }

            EquatorRing ring = data.Rings[ringIndex];
            if (!ring.IsOneFull() || !ring.isLowInsideRingComplete)
            {
                return false;
            }

            var rings = controllers.GetOrCreateValue(factory);
            if (!rings.TryGetValue(ringIndex, out ControllerState controller))
            {
                controller = new ControllerState();
                rings.Add(ringIndex, controller);
            }

            bool valid = IsController(factory, controller.EntityId, controller.AssemblerId);
            float now = Time.unscaledTime;
            if (now >= controller.NextProbe || (controller.EntityId > 0 && !valid))
            {
                controller.EntityId = 0;
                controller.AssemblerId = 0;
                controller.NextProbe = now + 0.25f;
                for (int slot = 0; slot < ring.Capacity; slot++)
                {
                    var pair = ring.GetPair(slot);
                    int assemblerId = pair.OrbitalStationPoolId;
                    if (pair.stationType != EquatorRing.StationType.Assembler
                        || assemblerId <= 0 || assemblerId >= factory.factorySystem.assemblerPool.Length)
                    {
                        continue;
                    }

                    int entityId = factory.factorySystem.assemblerPool[assemblerId].entityId;
                    if (!IsController(factory, entityId, assemblerId))
                    {
                        continue;
                    }

                    controller.EntityId = entityId;
                    controller.AssemblerId = assemblerId;
                    break;
                }

                valid = controller.EntityId > 0;
            }

            if (!valid || factory.factorySystem.assemblerPool[controller.AssemblerId].recipeId <= 0
                || controller.EntityId >= factory.entityAnimPool.Length)
            {
                return false;
            }

            // Cache controller identity only; simulation animation state remains live on every draw.
            AnimData animation = factory.entityAnimPool[controller.EntityId];
            return animation.state == 1 && animation.power >= 0.1f;
        }

        private static bool IsController(PlanetFactory factory, int entityId, int assemblerId)
        {
            if (entityId <= 0 || entityId >= factory.entityPool.Length
                || assemblerId <= 0 || assemblerId >= factory.factorySystem.assemblerPool.Length)
            {
                return false;
            }

            EntityData entity = factory.entityPool[entityId];
            AssemblerComponent assembler = factory.factorySystem.assemblerPool[assemblerId];
            return entity.id == entityId && entity.protoId == ProtoID.I星环对撞机 && entity.assemblerId == assemblerId
                && assembler.id == assemblerId && assembler.entityId == entityId;
        }

        internal static void Clear()
        {
            controllers = new ConditionalWeakTable<PlanetFactory, Dictionary<int, ControllerState>>();
        }
    }
}
