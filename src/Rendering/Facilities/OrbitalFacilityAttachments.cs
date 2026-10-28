using System;
using System.Collections.Generic;
using System.Diagnostics;
using ProjectOrbitalRing.Patches.Logic.OrbitalRing;
using ProjectOrbitalRing.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using static ProjectOrbitalRing.Patches.Logic.OrbitalRing.PosTool;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalFacilityAttachments
    {
        internal enum Surface
        {
            Armor,
            Recess,
            Metal,
            ServiceLight,
            BeamLight,
            PowerLight,
            Ceramic,
            Conductor,
            DetectorLens,
            BeamGuide0,
            BeamGuide1,
            BeamGuide2,
            AcceleratorArmor,
            PowerPanel,
            ConnectedLight,
            UnavailableLight,
        }

        private readonly struct SlotState
        {
            internal readonly int Ring;
            internal readonly int Slot;
            internal readonly int Entity;
            internal readonly int Item;
            internal readonly int Tracks;
            internal readonly bool Available;
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;

            internal SlotState(PlanetFactory factory, int ringIndex, int slot, EquatorRing ring, int functionalTracks)
            {
                Ring = ringIndex;
                Slot = slot;
                Entity = ResolveEntity(factory, ring, slot);
                Item = Entity > 0 ? factory.entityPool[Entity].protoId : 0;
                Available = OrbitalTextureLayout.IsFacilitySlotAvailable(factory.planet, ringIndex, slot);
                Tracks = (ring.isInsideRingComplete ? 1 : 0) | (ring.isOutsideRingComplete ? 2 : 0) | functionalTracks;
                Position = Entity > 0 ? factory.entityPool[Entity].pos : Vector3.zero;
                Rotation = Entity > 0 ? factory.entityPool[Entity].rot : Quaternion.identity;
            }

            internal bool Matches(SlotState other)
            {
                return Ring == other.Ring && Slot == other.Slot && Entity == other.Entity && Item == other.Item
                    && Tracks == other.Tracks && Available == other.Available
                    && Position == other.Position && Rotation == other.Rotation;
            }
        }

        private sealed class AttachmentBuild
        {
            internal PlanetFactory Factory;
            internal SlotState[] Slots;
            internal int Generation;
            internal readonly Mesh[] Meshes = new Mesh[(int)Surface.UnavailableLight + 1];
            internal readonly List<Mesh>[] VisibleMeshes = new List<Mesh>[(int)Surface.UnavailableLight + 1];
            internal IEnumerator<object> Work;
            internal bool Stale;
            internal int SocketCount;
            internal int OccupiedSlots;
            internal int BeamInterfaceCount;
            internal int PowerInterfaceCount;
            internal int TriangleCount;
        }

        private static PlanetFactory observedFactory;
        private static readonly List<SlotState> previous = new List<SlotState>();
        private static readonly List<SlotState> pending = new List<SlotState>();
        private static readonly Mesh[] meshes = new Mesh[(int)Surface.UnavailableLight + 1];
        private static readonly Mesh[][] visibleMeshes = new Mesh[(int)Surface.UnavailableLight + 1][];
        private static readonly List<Mesh> drawMeshes = new List<Mesh>();
        private static readonly Material[] materials = new Material[(int)Surface.UnavailableLight + 1];
        private static Material acceleratorSource;
        private static int[] localTracks = new int[0];
        private static bool tracksDirty = true;
        private static readonly MaterialPropertyBlock lighting = new MaterialPropertyBlock();
        private static float nextProbe;
        private static AttachmentBuild build;
        private static int generation;
        internal static bool IsBuilding => build != null;
        internal static int SocketCount { get; private set; }
        internal static int OccupiedSlots { get; private set; }
        internal static int BeamInterfaceCount { get; private set; }
        internal static int PowerInterfaceCount { get; private set; }
        internal static int TriangleCount { get; private set; }
        internal static int RebuildCount { get; private set; }

        internal static void Update()
        {
            PlanetFactory factory = GameMain.isRunning ? GameMain.localPlanet?.factory : null;
            if (factory == null)
            {
                if (observedFactory != null)
                {
                    Clear();
                }
                return;
            }
            if (!ReferenceEquals(factory, observedFactory))
            {
                Clear();
                observedFactory = factory;
            }

            if (Time.unscaledTime >= nextProbe)
            {
                ObserveSlots(factory);
            }

            AdvanceBuild(factory);
        }

        private static void ObserveSlots(PlanetFactory factory)
        {
            nextProbe = Time.unscaledTime + 0.25f;
            PlanetOrbitalRingData data = OrbitalStationManager.Instance.GetPlanetOrbitalRingData(factory.planetId);
            pending.Clear();
            if (data != null)
            {
                if (tracksDirty)
                {
                    ReadLocalTracks(factory);
                    tracksDirty = false;
                }

                for (int ringIndex = 0; ringIndex < data.Rings.Count; ringIndex++)
                {
                    EquatorRing ring = data.Rings[ringIndex];
                    for (int slot = 0; slot < ring.Capacity; slot++)
                    {
                        int index = ringIndex * GetLayout(factory.planet).StationCapacity + slot;
                        int presence = index < localTracks.Length ? localTracks[index] : 0;
                        pending.Add(new SlotState(factory, ringIndex, slot, ring, presence));
                    }
                }
            }

            IList<SlotState> expected = build == null ? (IList<SlotState>)previous : build.Slots;
            bool changed = pending.Count != expected.Count;
            for (int index = 0; !changed && index < pending.Count; index++)
            {
                changed = !pending[index].Matches(expected[index]);
            }

            if (!changed)
            {
                return;
            }
            CancelBuild();
            var candidate = new AttachmentBuild
            {
                Factory = factory,
                Slots = pending.ToArray(),
                Generation = ++generation,
            };
            candidate.Work = Rebuild(candidate).GetEnumerator();
            build = candidate;
        }

        private static void AdvanceBuild(PlanetFactory factory)
        {
            while (build != null && OrbitalMeshChunks.HasBuildBudget)
            {
                var candidate = build;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    if (candidate.Work.MoveNext())
                    {
                        continue;
                    }

                    if (candidate.Stale)
                    {
                        CancelBuild();
                        nextProbe = 0;
                        break;
                    }

                    ObserveSlots(factory);
                    if (ReferenceEquals(build, candidate) && candidate.Generation == generation)
                    {
                        Publish(candidate);
                        CancelBuild();
                    }
                }
                catch
                {
                    if (ReferenceEquals(build, candidate))
                    {
                        CancelBuild();
                    }
                    throw;
                }
                finally
                {
                    OrbitalMeshChunks.RecordBuildTime(started);
                }
            }
        }

        private static void Publish(AttachmentBuild candidate)
        {
            var chunks = new Mesh[meshes.Length][];
            for (int index = 0; index < chunks.Length; index++)
            {
                chunks[index] = candidate.VisibleMeshes[index]?.ToArray();
            }

            // Candidates retain ownership until every surface is ready for publication.
            ReleaseMeshes();
            for (int index = 0; index < meshes.Length; index++)
            {
                meshes[index] = candidate.Meshes[index];
                candidate.Meshes[index] = null;
                if (candidate.VisibleMeshes[index] != null)
                {
                    visibleMeshes[index] = chunks[index];
                    candidate.VisibleMeshes[index].Clear();
                    drawMeshes.Capacity = Math.Max(drawMeshes.Capacity, visibleMeshes[index].Length);
                }
            }

            previous.Clear();
            previous.AddRange(candidate.Slots);
            SocketCount = candidate.SocketCount;
            OccupiedSlots = candidate.OccupiedSlots;
            BeamInterfaceCount = candidate.BeamInterfaceCount;
            PowerInterfaceCount = candidate.PowerInterfaceCount;
            TriangleCount = candidate.TriangleCount;
            RebuildCount++;
        }

        private static void CancelBuild()
        {
            var candidate = build;
            build = null;
            if (candidate == null)
            {
                return;
            }

            candidate.Work.Dispose();
            for (int index = 0; index < candidate.Meshes.Length; index++)
            {
                OrbitalMeshChunks.Destroy(candidate.VisibleMeshes[index]);
                candidate.VisibleMeshes[index]?.Clear();
                candidate.VisibleMeshes[index] = null;
                if (candidate.Meshes[index] != null)
                {
                    UnityEngine.Object.Destroy(candidate.Meshes[index]);
                    candidate.Meshes[index] = null;
                }
            }
        }

        private static int ResolveEntity(PlanetFactory factory, EquatorRing ring, int slot)
        {
            var pair = ring.GetPair(slot);
            int poolId = pair.OrbitalStationPoolId;
            if (poolId <= 0)
            {
                return 0;
            }
            int entityId;
            if (pair.stationType == EquatorRing.StationType.Station)
            {
                var pool = factory.transport.stationPool;
                entityId = poolId < pool.Length && pool[poolId] != null ? pool[poolId].entityId : 0;
            }
            else if (pair.stationType == EquatorRing.StationType.Assembler)
            {
                var pool = factory.factorySystem.assemblerPool;
                entityId = poolId < pool.Length ? pool[poolId].entityId : 0;
            }
            else
            {
                entityId = poolId;
            }

            return entityId > 0 && entityId < factory.entityPool.Length && factory.entityPool[entityId].id == entityId
                ? entityId : 0;
        }

        internal static void InvalidateTracks(PlanetFactory factory)
        {
            if (factory == observedFactory)
            {
                tracksDirty = true;
                nextProbe = 0;
            }
        }

        private static void ReadLocalTracks(PlanetFactory factory)
        {
            // Station annexes follow local built segments; ring suspension follows circuit completion.
            var layout = GetLayout(factory.planet);
            int count = layout.StationCapacity * layout.StationRingCount;
            if (localTracks.Length != count)
            {
                localTracks = new int[count];
            }
            else
            {
                Array.Clear(localTracks, 0, localTracks.Length);
            }

            for (int entityId = 1; entityId < factory.entityCursor; entityId++)
            {
                ref EntityData entity = ref factory.entityPool[entityId];
                if (entity.id != entityId || (entity.protoId != ProtoID.I粒子加速轨道 && entity.protoId != ProtoID.I星环电网组件))
                {
                    continue;
                }

                var position = CalculateRingPosMark(entity.pos, layout);
                if (position.ringIndex < 0)
                {
                    continue;
                }

                float coordinate = OrbitalTextureLayout.FacilityCoordinate(factory.planet, entity.pos);
                int slot = Mathf.RoundToInt(coordinate) % layout.StationCapacity;
                float longitude = OrbitalTextureLayout.FacilityLongitude(factory.planet, slot);
                float actualLongitude = Mathf.Atan2(entity.pos.z, entity.pos.x);
                float angle = Mathf.DeltaAngle(longitude * Mathf.Rad2Deg, actualLongitude * Mathf.Rad2Deg);
                float distance = Mathf.Abs(angle) * Mathf.Deg2Rad * new Vector2(entity.pos.x, entity.pos.z).magnitude;
                if (distance <= 14f)
                {
                    localTracks[position.ringIndex * layout.StationCapacity + slot] |=
                        entity.protoId == ProtoID.I粒子加速轨道 ? 4 : 8;
                }
            }
        }

        private static IEnumerable<object> Rebuild(AttachmentBuild candidate)
        {
            PlanetFactory factory = candidate.Factory;
            var builders = new OrbitalAttachmentMesh[meshes.Length];
            for (int index = 0; index < builders.Length; index++)
            {
                builders[index] = new OrbitalAttachmentMesh();
            }
            foreach (SlotState slot in candidate.Slots)
            {
                if (!MatchesEntity(factory, slot))
                {
                    candidate.Stale = true;
                    yield break;
                }

                float longitude = OrbitalTextureLayout.FacilityLongitude(factory.planet, slot.Slot);
                bool occupied = slot.Entity > 0;
                OrbitalFacilityGeometry.SeatState seatState = occupied ? OrbitalFacilityGeometry.SeatState.Connected
                    : !slot.Available ? OrbitalFacilityGeometry.SeatState.Unavailable
                    : OrbitalFacilityGeometry.SeatState.Vacant;
                Vector2 seatLongitudes = OrbitalFacilityGeometry.SeatLongitudes(factory, slot.Slot);
                for (int band = 1; band <= 2; band++)
                {
                    if ((slot.Tracks & band) == 0)
                    {
                        continue;
                    }

                    foreach (float sign in new[] { -1f, 1f })
                    {
                        Vector3 center = OrbitalTextureLayout.TrackCenter(factory, slot.Ring, band,
                            sign < 0 ? seatLongitudes.x : seatLongitudes.y, ProtoID.I轨道连接组件);
                        Vector3 up = center.normalized;
                        Vector3 along = Vector3.Cross(up, Vector3.up).normalized * sign;
                        Vector3 across = Vector3.Cross(along, up).normalized;
                        OrbitalFacilityGeometry.Seat(builders, center, across, up, along, seatState);
                        candidate.SocketCount++;
                        yield return null;
                    }
                }

                if (occupied && (slot.Tracks & 3) != 0)
                {
                    if (!MatchesEntity(factory, slot))
                    {
                        candidate.Stale = true;
                        yield break;
                    }
                    OrbitalFacilityGeometry.Support(builders, factory, slot.Ring, longitude,
                        slot.Entity, slot.Tracks, seatLongitudes);
                    candidate.OccupiedSlots++;
                    yield return null;
                }

                if (occupied && (slot.Tracks & 4) != 0)
                {
                    if (!MatchesEntity(factory, slot))
                    {
                        candidate.Stale = true;
                        yield break;
                    }
                    if (OrbitalFacilityBays.Beam(builders, factory, slot.Ring, longitude, slot.Entity))
                    {
                        candidate.BeamInterfaceCount++;
                    }
                    yield return null;
                }

                if (occupied && (slot.Tracks & 8) != 0)
                {
                    if (!MatchesEntity(factory, slot))
                    {
                        candidate.Stale = true;
                        yield break;
                    }
                    if (OrbitalFacilityBays.Power(builders, factory, slot.Ring, longitude, slot.Entity))
                    {
                        candidate.PowerInterfaceCount++;
                    }
                    yield return null;
                }

                yield return null;
            }

            for (int index = 0; index < builders.Length; index++)
            {
                if (builders[index].TriangleCount == 0)
                {
                    continue;
                }
                candidate.Meshes[index] = builders[index].Build(
                    index == (int)Surface.AcceleratorArmor || index == (int)Surface.PowerPanel);
                candidate.Meshes[index].name = "OrbitalRing.FacilityAttachments." + (Surface)index;
                yield return null;
                var chunks = new List<Mesh>();
                candidate.VisibleMeshes[index] = chunks;
                foreach (var step in OrbitalMeshChunks.Create(candidate.Meshes[index], chunks, 64))
                {
                    yield return step;
                }

                candidate.TriangleCount += builders[index].TriangleCount;
                yield return null;
            }
        }

        private static bool MatchesEntity(PlanetFactory factory, SlotState slot)
        {
            if (slot.Entity == 0)
            {
                return true;
            }

            if (slot.Entity >= factory.entityPool.Length)
            {
                return false;
            }

            ref EntityData entity = ref factory.entityPool[slot.Entity];
            return entity.id == slot.Entity && entity.protoId == slot.Item && entity.pos == slot.Position
                && entity.rot == slot.Rotation;
        }

        internal static void Draw(PlanetFactory factory, Camera camera)
        {
            var model = factory?.planet?.factoryModel;
            if (factory != observedFactory || camera == null || model == null
                || model.disableFactoryEntities || model.gpuiManager == null || !model.gpuiManager.renderEntity)
            {
                return;
            }
            OrbitalRenderBounds.Prepare(camera);
            float planetRadius = factory.planet.realRadius - 2;
            for (int index = 0; index < meshes.Length; index++)
            {
                Mesh mesh = meshes[index];
                if (mesh == null)
                {
                    continue;
                }
                bool guide = index >= (int)Surface.BeamGuide0 && index <= (int)Surface.BeamGuide2;
                bool signal = IsSignalSurface((Surface)index);
                Material material = MaterialFor((Surface)index);
                lighting.Clear();
                TrackRendering.ApplyMetalLighting(lighting, camera.transform.position.normalized);
                if (index == (int)Surface.AcceleratorArmor)
                {
                    lighting.SetColor("_EmissionColor", Color.white * 0.8f);
                }

                if (signal)
                {
                    Color tint = SignalTint((Surface)index, emission: true);
                    float intensity = index == (int)Surface.UnavailableLight
                        ? 1 : 1.2f + Mathf.Sin(Time.time * 1.4f) * 0.15f;
                    if (guide)
                    {
                        int phase = index - (int)Surface.BeamGuide0;
                        float wave = 0.5f + Mathf.Sin(Time.time * 2.2f - phase * Mathf.PI * 2 / 3) * 0.5f;
                        intensity = 0.8f + Mathf.Pow(wave, 3) * 2.1f;
                    }

                    lighting.SetColor("_EmissionColor", tint * intensity);
                }

                Mesh[] chunks = visibleMeshes[index];
                if (chunks == null)
                {
                    continue;
                }

                drawMeshes.Clear();
                foreach (Mesh chunk in chunks)
                {
                    if (OrbitalRenderBounds.IsVisible(camera, chunk.bounds, planetRadius))
                    {
                        drawMeshes.Add(chunk);
                    }
                }

                if (drawMeshes.Count * 3 > chunks.Length)
                {
                    Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, camera, 0, lighting,
                        signal ? ShadowCastingMode.Off : ShadowCastingMode.On, !signal);
                    continue;
                }

                if (!signal)
                {
                    Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, camera, 0, lighting,
                        ShadowCastingMode.ShadowsOnly, false);
                }

                foreach (Mesh chunk in drawMeshes)
                {
                    Graphics.DrawMesh(chunk, Matrix4x4.identity, material, 0, camera, 0, lighting,
                        ShadowCastingMode.Off, !signal);
                }
            }
        }

        private static Material MaterialFor(Surface surface)
        {
            int index = (int)surface;
            if (surface == Surface.PowerPanel)
            {
                if (materials[index] == null)
                {
                    materials[index] = OrbitalPowerMaterial.Create();
                }

                return materials[index];
            }

            if (surface == Surface.AcceleratorArmor && acceleratorSource != OrbitalTrackAppearance.AcceleratorArmor)
            {
                if (materials[index] != null)
                {
                    UnityEngine.Object.Destroy(materials[index]);
                    materials[index] = null;
                }

                acceleratorSource = OrbitalTrackAppearance.AcceleratorArmor;
            }

            if (materials[index] != null)
            {
                if (surface == Surface.Armor && materials[index].mainTexture == null)
                {
                    materials[index].mainTexture = OrbitalTrackAppearance.ArmorTexture;
                }

                return materials[index];
            }

            var material = new Material(surface == Surface.AcceleratorArmor && acceleratorSource != null
                ? acceleratorSource : OrbitalRingBraces.Metal)
            {
                name = "OrbitalRing.Facility." + surface,
            };
            if (surface == Surface.Armor)
            {
                material.mainTexture = OrbitalTrackAppearance.ArmorTexture;
                material.color = new Color(0.88f, 0.91f, 0.95f);
                material.SetFloat("_Metallic", 0.26f);
                material.SetFloat("_Glossiness", 0.56f);
                material.SetColor("_EmissionColor", new Color(0.045f, 0.048f, 0.052f));
            }
            else if (surface == Surface.Recess)
            {
                material.color = new Color(0.15f, 0.18f, 0.22f);
                material.SetFloat("_Glossiness", 0.4f);
            }
            else if (surface == Surface.Ceramic)
            {
                material.color = new Color(0.88f, 0.92f, 0.90f);
                material.SetFloat("_Metallic", 0.02f);
                material.SetFloat("_Glossiness", 0.45f);
                material.SetColor("_EmissionColor", new Color(0.30f, 0.32f, 0.31f));
            }
            else if (surface == Surface.Conductor)
            {
                material.color = new Color(0.64f, 0.38f, 0.18f);
            }
            else if (surface == Surface.DetectorLens)
            {
                material.color = new Color(0.085f, 0.055f, 0.15f);
                material.SetFloat("_Glossiness", 0.91f);
                material.SetFloat("_Metallic", 0.40f);
                material.SetColor("_EmissionColor", new Color(0.025f, 0.008f, 0.05f));
            }
            else if (IsSignalSurface(surface))
            {
                material.color = SignalTint(surface, emission: false);
                material.SetFloat("_Metallic", 0.15f);
            }

            materials[index] = material;
            return material;
        }

        private static bool IsSignalSurface(Surface surface)
        {
            return surface == Surface.ServiceLight || surface == Surface.BeamLight || surface == Surface.PowerLight
                || surface == Surface.ConnectedLight || surface == Surface.UnavailableLight
                || (surface >= Surface.BeamGuide0 && surface <= Surface.BeamGuide2);
        }

        private static Color SignalTint(Surface surface, bool emission)
        {
            switch (surface)
            {
                case Surface.ServiceLight:
                    return emission ? new Color(1, 0.58f, 0.09f) : new Color(1, 0.69f, 0.16f);
                case Surface.PowerLight:
                case Surface.ConnectedLight:
                    return emission ? new Color(0.1f, 0.95f, 0.26f) : new Color(0.15f, 0.8f, 0.3f);
                case Surface.UnavailableLight:
                    return emission ? new Color(0.12f, 0.13f, 0.14f) : new Color(0.42f, 0.44f, 0.46f);
                default:
                    return emission ? new Color(0.62f, 0.27f, 1) : new Color(0.65f, 0.42f, 0.95f);
            }
        }

        private static void ReleaseMeshes()
        {
            drawMeshes.Clear();
            for (int index = 0; index < meshes.Length; index++)
            {
                OrbitalMeshChunks.Destroy(visibleMeshes[index]);
                visibleMeshes[index] = null;
                if (meshes[index] != null)
                {
                    UnityEngine.Object.Destroy(meshes[index]);
                }
                meshes[index] = null;
            }
        }

        internal static void Clear()
        {
            CancelBuild();
            generation++;
            ReleaseMeshes();
            previous.Clear();
            pending.Clear();
            observedFactory = null;
            nextProbe = 0;
            tracksDirty = true;
            SocketCount = 0;
            OccupiedSlots = 0;
            BeamInterfaceCount = 0;
            PowerInterfaceCount = 0;
            TriangleCount = 0;
        }

        internal static void Dispose()
        {
            Clear();
            for (int index = 0; index < materials.Length; index++)
            {
                if (materials[index] != null)
                {
                    UnityEngine.Object.Destroy(materials[index]);
                }
                materials[index] = null;
            }

        }
    }
}
