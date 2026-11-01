using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace ProjectOrbitalRing.Patches.Logic.OrbitalRing
{
    [HarmonyPatch]
    internal static class StructuralTrackCargo
    {
        private sealed class TrafficState
        {
            internal readonly CargoTraffic Traffic;
            internal int Revision;

            internal TrafficState(CargoTraffic traffic) => Traffic = traffic;
        }

        private sealed class PathState
        {
            internal readonly TrafficState Owner;
            internal int Version;
            internal int Revision = -1;
            internal int OwnerRevision = -1;
            internal bool ContainsTrack;

            internal PathState(TrafficState owner) => Owner = owner;
        }

        private static readonly ConditionalWeakTable<CargoContainer, TrafficState> owners =
            new ConditionalWeakTable<CargoContainer, TrafficState>();
        private static readonly ConditionalWeakTable<CargoPath, PathState> paths =
            new ConditionalWeakTable<CargoPath, PathState>();

        private static TrafficState Bind(CargoTraffic traffic)
        {
            return traffic?.container == null ? null
                : owners.GetValue(traffic.container, _ => new TrafficState(traffic));
        }

        private static void Invalidate(CargoPath path)
        {
            if (path != null && paths.TryGetValue(path, out PathState state))
            {
                Interlocked.Increment(ref state.Version);
            }
        }

        private static bool BlocksCargo(CargoPath path)
        {
            if (!StructuralTrackPolicy.Enabled || path?.cargoContainer == null || path.id <= 0)
            {
                return false;
            }
            if (!paths.TryGetValue(path, out PathState state))
            {
                if (!owners.TryGetValue(path.cargoContainer, out TrafficState owner))
                {
                    return false;
                }
                state = paths.GetValue(path, _ => new PathState(owner));
            }
            int ownerRevision = Volatile.Read(ref state.Owner.Revision);
            int version = Volatile.Read(ref state.Version);
            if (Volatile.Read(ref state.Revision) != version || state.OwnerRevision != ownerRevision)
            {
                lock (state)
                {
                    ownerRevision = Volatile.Read(ref state.Owner.Revision);
                    version = Volatile.Read(ref state.Version);
                    bool containsTrack = path.belts.Count == 0;
                    try
                    {
                        foreach (int beltId in path.belts)
                        {
                            if (StructuralTrackPolicy.IsTrackBelt(state.Owner.Traffic, beltId))
                            {
                                containsTrack = true;
                                break;
                            }
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        return true;
                    }
                    if (ownerRevision != Volatile.Read(ref state.Owner.Revision) ||
                        version != Volatile.Read(ref state.Version))
                    {
                        return true;
                    }
                    state.ContainsTrack = containsTrack;
                    state.OwnerRevision = ownerRevision;
                    Volatile.Write(ref state.Revision, version);
                }
            }
            bool blocked = state.ContainsTrack;
            return ownerRevision != Volatile.Read(ref state.Owner.Revision) ||
                version != Volatile.Read(ref state.Version) || blocked;
        }

        internal static bool CanNormalize(PlanetFactory factory, IReadOnlyCollection<int> entityIds)
        {
            if (!StructuralTrackPolicy.Enabled || factory?.cargoTraffic == null)
            {
                return true;
            }
            CargoTraffic traffic = factory.cargoTraffic;
            Bind(traffic);
            if (traffic.container.tmpEnabled || traffic.container.tmpCargoCount > 0)
            {
                return false;
            }
            var affected = new HashSet<CargoPath>();
            foreach (int entityId in entityIds)
            {
                if (entityId <= 0 || !StructuralTrackPolicy.IsTrackObject(factory, entityId))
                {
                    continue;
                }
                int beltId = factory.entityPool[entityId].beltId;
                if (!StructuralTrackPolicy.IsTrackBelt(traffic, beltId))
                {
                    continue;
                }
                int pathId = traffic.beltPool[beltId].segPathId;
                if (pathId > 0 && pathId < traffic.pathPool.Length && traffic.pathPool[pathId]?.id == pathId)
                {
                    affected.Add(traffic.pathPool[pathId]);
                }
            }
            foreach (CargoPath path in affected)
            {
                lock (path.buffer)
                {
                    for (int index = 4; index + 5 < path.bufferLength; index++)
                    {
                        if (path.buffer[index] == CargoPath.kCargoSign)
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.Import))]
        private static void Importing(CargoTraffic __instance)
        {
            TrafficState owner = Bind(__instance);
            if (owner != null)
            {
                Interlocked.Increment(ref owner.Revision);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.Import))]
        private static void Imported(CargoTraffic __instance) => Importing(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.NewCargoPath))]
        private static void CreatedPath(CargoTraffic __instance, CargoPath __result)
        {
            Bind(__instance);
            Invalidate(__result);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CargoTraffic), nameof(CargoTraffic.UpgradeBeltComponent))]
        private static void UpgradedBelt(CargoTraffic __instance, int beltId)
        {
            if (beltId > 0 && beltId < __instance.beltPool.Length)
            {
                int pathId = __instance.beltPool[beltId].segPathId;
                if (pathId > 0 && pathId < __instance.pathPool.Length)
                {
                    Invalidate(__instance.pathPool[pathId]);
                }
            }
        }

        [HarmonyPatch]
        private static class PathMutation
        {
            [HarmonyTargetMethods]
            private static IEnumerable<MethodBase> Targets()
            {
                foreach (string name in new[] { "AddRelatedBelt", "RemoveRelatedBelt", "PathConcat",
                    "PathCopy", "TruncBuffer", "Clear", "Free", "Import" })
                {
                    yield return AccessTools.Method(typeof(CargoPath), name);
                }
            }

            [HarmonyPostfix]
            private static void After(CargoPath __instance)
            {
                Invalidate(__instance);
            }

            [HarmonyPrefix]
            private static void Before(CargoPath __instance) => Invalidate(__instance);
        }

        [HarmonyPatch]
        private static class Insertion
        {
            [HarmonyTargetMethods]
            private static IEnumerable<MethodBase> Targets()
            {
                var names = new HashSet<string> { "TryInsertCargo", "TryInsertCargoNoSqueeze", "TryInsertItem",
                    "TryInsertItemAtHead", "TryInsertItemAtHeadAndFillBlank", "TryUpdateItemAtHeadAndFillBlank",
                    "InsertItemDirectWithFastCheck" };
                foreach (MethodInfo method in typeof(CargoPath).GetMethods())
                {
                    if (names.Contains(method.Name))
                    {
                        yield return method;
                    }
                }
            }

            [HarmonyPrefix]
            private static bool Before(CargoPath __instance, ref bool __result)
            {
                if (!BlocksCargo(__instance))
                {
                    return true;
                }
                __result = false;
                return false;
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CargoPath), nameof(CargoPath.TryInsertItemWithStackIncreasement))]
        private static bool StackedInsertion(CargoPath __instance) => !BlocksCargo(__instance);

        [HarmonyPrefix, HarmonyPatch(typeof(CargoPath), nameof(CargoPath.TestBlankAtHead))]
        private static bool HeadSpace(CargoPath __instance, ref int __result)
        {
            if (!BlocksCargo(__instance))
            {
                return true;
            }
            __result = -1;
            return false;
        }

        [HarmonyPatch]
        private static class EmptySimulation
        {
            [HarmonyTargetMethods]
            private static IEnumerable<MethodBase> Targets()
            {
                yield return AccessTools.Method(typeof(CargoPath), nameof(CargoPath.Update));
                yield return AccessTools.Method(typeof(CargoPath), nameof(CargoPath.PresentCargos));
            }

            [HarmonyPrefix]
            private static bool Before(CargoPath __instance) => !BlocksCargo(__instance);
        }

        [HarmonyPatch]
        private static class CargoIdQuery
        {
            [HarmonyTargetMethods]
            private static IEnumerable<MethodBase> Targets()
            {
                foreach (string name in new[] { "GetCargoIdAtRear", "GetCargoIdAtIndex", "TryPickCargoAtEnd",
                    "TryPickCargo" })
                {
                    yield return AccessTools.Method(typeof(CargoPath), name);
                }
            }

            [HarmonyPrefix]
            private static bool Before(CargoPath __instance, ref int __result)
            {
                if (!BlocksCargo(__instance))
                {
                    return true;
                }
                __result = -1;
                return false;
            }
        }

        [HarmonyPatch]
        private static class CargoPresenceQuery
        {
            [HarmonyTargetMethods]
            private static IEnumerable<MethodBase> Targets()
            {
                foreach (string name in new[] { "HasCargoAtRear", "TestCargoAtRear", "CanPickItemFromRear" })
                {
                    yield return AccessTools.Method(typeof(CargoPath), name);
                }
            }

            [HarmonyPrefix]
            private static bool Before(CargoPath __instance, ref bool __result)
            {
                if (!BlocksCargo(__instance))
                {
                    return true;
                }
                __result = false;
                return false;
            }
        }

        [HarmonyPatch]
        private static class ItemQuery
        {
            [HarmonyTargetMethods]
            private static IEnumerable<MethodBase> Targets()
            {
                foreach (MethodInfo method in typeof(CargoPath).GetMethods())
                {
                    if (method.Name == "TryPickItem" || method.Name == "TryPickFuel" ||
                        method.Name == "QueryItemAtIndex")
                    {
                        yield return method;
                    }
                }
            }

            [HarmonyPrefix]
            private static bool Before(CargoPath __instance, ref byte stack, ref byte inc, ref int __result)
            {
                if (!BlocksCargo(__instance))
                {
                    return true;
                }
                stack = 0;
                inc = 0;
                __result = 0;
                return false;
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CargoPath), nameof(CargoPath.TryPickItemAtRear))]
        private static bool RearItem(CargoPath __instance, ref int needIdx, ref byte stack, ref byte inc,
            ref int __result)
        {
            if (!BlocksCargo(__instance))
            {
                return true;
            }
            needIdx = -1;
            stack = 0;
            inc = 0;
            __result = 0;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(CargoPath), nameof(CargoPath.GetCargoAtIndex))]
        private static bool CargoAtIndex(CargoPath __instance, ref Cargo cargo, ref int cargoId, ref int offset,
            ref bool __result)
        {
            if (!BlocksCargo(__instance))
            {
                return true;
            }
            cargo = default;
            cargoId = -1;
            offset = -1;
            __result = false;
            return false;
        }
    }
}
