using System;
using System.Collections.Generic;
using ProjectOrbitalRing.Patches.Logic.OrbitalRing;
using ProjectOrbitalRing.Utils;
using UnityEngine;
using static ProjectOrbitalRing.Patches.Logic.OrbitalRing.PosTool;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalTrackVisuals
    {
        private static readonly Dictionary<long, RingState> ringStates = new Dictionary<long, RingState>();
        private static readonly int emissionColor = Shader.PropertyToID("_EmissionColor");
        private static readonly int beamTexture = Shader.PropertyToID("_MainTex");
        private static readonly int emissionTexture = Shader.PropertyToID("_EmissionMap");
        private static readonly int textureTransform = Shader.PropertyToID("_MainTex_ST");
        private static readonly int detailTextureTransform = Shader.PropertyToID("_DetailAlbedoMap_ST");
        private static GameData observedGame;
        private static bool scanLoadedFactories = true;
        private static bool registered;

        private sealed class RingState
        {
            internal PlanetFactory Factory;
            internal int ItemId;
            internal int Ring;
            internal int Band;
            internal bool? Complete;
        }

        internal static void Register()
        {
            if (registered)
            {
                return;
            }

            TrackModelAssets.Initialize();
            RegisterType(
                ProtoID.I轨道连接组件, new Color(1f, 0.60f, 0.08f), false, new TrackGeometryProfile(2f, 1.08f, 0.7f));
            RegisterType(
                ProtoID.I星环电网组件, new Color(0.10f, 1f, 0.28f), false, new TrackGeometryProfile(1.35f, 0.98f, 0.7f));
            RegisterType(
                ProtoID.I粒子加速轨道, new Color(0.58f, 0.24f, 0.88f), true, new TrackGeometryProfile(1.65f, 1.28f, 0.7f));
            TrackRenderer.ConnectionsChanged += Schedule;
            registered = true;
        }

        private static void RegisterType(
            int itemId, Color lightColor, bool particleTrack, TrackGeometryProfile geometry)
        {
            TrackWallMode wallMode = particleTrack ? TrackWallMode.Glass : TrackWallMode.Open;
            Action<TrackMaterialSet> configureMaterials = materials =>
                OrbitalTrackAppearance.Configure(materials, itemId, lightColor);
            Action<TrackEffectContext, MaterialPropertyBlock> updateEffects = (context, properties) =>
                ApplyEffects(context, properties, itemId, lightColor, geometry);

            var appearance = new TrackAppearance(wallMode, configureMaterials, updateEffects,
                OrbitalTextureLayout.TextureCoordinate, OrbitalTextureLayout.CoordinatePeriod,
                wallRadiusFraction: particleTrack ? 0.66f : 1, receiveShadows: false);
            TrackRenderer.Register(itemId, appearance, geometry, BatchGroup);
        }

        private static int RingGroup(PlanetFactory factory, int entityId)
        {
            var position = CalculateRingPosMark(factory.entityPool[entityId].pos, GetLayout(factory.planet));
            return position.ringIndex * 4 + position.ringBeltIndex;
        }

        private static int BatchGroup(PlanetFactory factory, int entityId)
        {
            bool particleTrack = factory.entityPool[entityId].protoId == ProtoID.I粒子加速轨道;
            int direction = particleTrack ? TravelDirection(factory, entityId) : 1;
            // A draw batch shares one animation direction and one ring activation state.
            return RingGroup(factory, entityId) * 2 + (direction < 0 ? 1 : 0);
        }

        private static int TravelDirection(PlanetFactory factory, int entityId)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                factory.ReadObjectConn(entityId, slot, out bool output, out int neighbor, out _);
                if (!IsEntity(factory, neighbor) || factory.entityPool[neighbor].protoId != ProtoID.I粒子加速轨道)
                {
                    continue;
                }

                Vector3 from = factory.entityPool[output ? entityId : neighbor].pos;
                Vector3 to = factory.entityPool[output ? neighbor : entityId].pos;
                float delta = Mathf.DeltaAngle(Longitude(from), Longitude(to));
                return delta < 0 ? -1 : 1;
            }

            return 1;
        }

        private static void ApplyEffects(TrackEffectContext context, MaterialPropertyBlock properties,
            int itemId, Color lightColor, TrackGeometryProfile geometry)
        {
            bool wallSurface =
                context.Surface == TrackSurfaceRole.Wall || context.Surface == TrackSurfaceRole.InnerWall;
            bool frameSurface =
                context.Surface == TrackSurfaceRole.Fittings || context.Surface == TrackSurfaceRole.Detail;
            if (context.Surface != TrackSurfaceRole.Core && !wallSurface && !frameSurface)
            {
                return;
            }

            int ringGroup = context.GroupKey / 2;
            int band = ringGroup % 4;
            if (context.GroupKey < 0 || (band != 1 && band != 2))
            {
                return;
            }

            var textureLayout = OrbitalTextureLayout.FromBand(
                context.Factory.planet, itemId, band, geometry.CenterHeight);
            properties.SetVector(detailTextureTransform, new Vector4(1, textureLayout.UvScale, 0, 0));
            bool particle = itemId == ProtoID.I粒子加速轨道;
            bool complete = IsComplete(context.Factory, ringGroup, itemId);
            bool active = complete && (!particle || OrbitalColliderActivity.IsWorking(context.Factory, ringGroup / 4));
            float speed = active ? 2.5f : 0.5f;
            float direction = context.GroupKey % 2 == 0 ? 1 : -1;
            float offset = 0;
            if (particle && context.Surface == TrackSurfaceRole.Core)
            {
                float beamPhaseSpeed = textureLayout.DistanceToPhase(1.8f);
                offset = -context.TimeSeconds * beamPhaseSpeed * direction * speed;
            }

            float uvScale = textureLayout.UvScale;
            if (particle && frameSurface)
            {
                uvScale = 1;
            }
            else if (itemId == ProtoID.I星环电网组件 && context.Surface == TrackSurfaceRole.Core)
            {
                int repeats = Math.Max(1, Mathf.RoundToInt(textureLayout.RepeatCount / 2f));
                uvScale = repeats / OrbitalTextureLayout.CoordinatePeriod;
            }
            else if (itemId == ProtoID.I轨道连接组件 && context.Surface == TrackSurfaceRole.Fittings)
            {
                // Panel patterns span two truss bays, with five periods per facility interval.
                uvScale /= 2;
            }

            properties.SetVector(textureTransform, new Vector4(1, uvScale, 0, offset));
            float pulse = 0.86f + Mathf.Sin(context.TimeSeconds * 1.8f) * 0.14f;
            float intensity = 1.3f;
            if (frameSurface)
            {
                intensity = 0.8f;
            }
            else if (particle)
            {
                intensity = 4.8f;
            }
            else if (itemId == ProtoID.I轨道连接组件)
            {
                intensity = OrbitalTrackAppearance.StructureLightIntensity;
            }

            bool whiteEmission = frameSurface || itemId == ProtoID.I星环电网组件 || particle;
            Color emissionTint = whiteEmission ? Color.white : lightColor;
            float standbyIntensity = particle && frameSurface ? 0.55f : 0.18f;
            float emissionStrength = 0;
            if (complete)
            {
                emissionStrength = active ? intensity * pulse : standbyIntensity;
            }

            properties.SetColor(emissionColor, emissionTint * emissionStrength);
            if (particle && context.Surface == TrackSurfaceRole.Core)
            {
                Texture texture;
                if (active)
                {
                    texture = OrbitalParticleBeamMaterial.WorkingTexture;
                }
                else
                {
                    texture = OrbitalParticleBeamMaterial.StandbyTexture;
                }

                if (texture != null)
                {
                    properties.SetTexture(beamTexture, texture);
                }

                float beamIntensity = complete ? 0.95f : 0;
                if (active)
                {
                    beamIntensity = 1.65f * (1 + Mathf.Sin(context.TimeSeconds * 5) * 0.06f);
                }

                properties.SetColor("_TintColor", Color.white * beamIntensity);
            }

            if (particle && context.Surface == TrackSurfaceRole.Detail)
            {
                Texture texture;
                if (active)
                {
                    texture = OrbitalTrackAppearance.AcceleratorWorkingEmission;
                }
                else
                {
                    texture = OrbitalTrackAppearance.AcceleratorStandbyEmission;
                }

                if (texture != null)
                {
                    properties.SetTexture(emissionTexture, texture);
                }
            }
        }

        private static bool IsComplete(PlanetFactory factory, int ringGroup, int itemId)
        {
            PlanetOrbitalRingData data = OrbitalStationManager.Instance.GetPlanetOrbitalRingData(factory.planet.id);
            int ringIndex = ringGroup / 4;
            if (ringGroup < 0 || data == null || ringIndex >= data.Rings.Count)
            {
                return false;
            }

            EquatorRing ring = data.Rings[ringIndex];
            if (itemId == ProtoID.I粒子加速轨道)
            {
                return ring.isLowInsideRingComplete;
            }

            if (itemId == ProtoID.I星环电网组件)
            {
                return ring.isLowOutsideRingComplete;
            }

            return ringGroup % 4 == 1 ? ring.isInsideRingComplete : ring.isOutsideRingComplete;
        }

        internal static void Schedule(PlanetFactory factory, int entityId)
        {
            ObserveGame();
            if (!IsEntity(factory, entityId))
            {
                OrbitalFacilityAttachments.InvalidateTracks(factory);
                return;
            }

            int itemId = factory.entityPool[entityId].protoId;
            if (!IsOrbitalItem(itemId))
            {
                return;
            }

            OrbitalFacilityAttachments.InvalidateTracks(factory);

            var position = CalculateRingPosMark(factory.entityPool[entityId].pos, GetLayout(factory.planet));
            if (position.ringIndex < 0 || position.ringBeltIndex < 0)
            {
                return;
            }

            int ringBand = position.ringIndex * 32 + position.ringBeltIndex * 8;
            uint typeKey = (uint)(ringBand + itemId - ProtoID.I轨道连接组件);
            long key = ((long)factory.planet.id << 32) | typeKey;
            if (!ringStates.TryGetValue(key, out RingState state))
            {
                state = new RingState
                {
                    Factory = factory,
                    ItemId = itemId,
                    Ring = position.ringIndex,
                    Band = position.ringBeltIndex,
                };
                ringStates.Add(key, state);
            }
        }

        internal static void Update()
        {
            ObserveGame();
            if (!GameMain.isRunning || !registered)
            {
                return;
            }

            if (scanLoadedFactories)
            {
                scanLoadedFactories = false;
                for (int factoryIndex = 0; factoryIndex < GameMain.data.factoryCount; factoryIndex++)
                {
                    PlanetFactory factory = GameMain.data.factories[factoryIndex];
                    if (factory == null)
                    {
                        continue;
                    }

                    for (int entityId = 1; entityId < factory.entityCursor; entityId++)
                    {
                        if (factory.entityPool[entityId].id == entityId
                            && IsOrbitalItem(factory.entityPool[entityId].protoId))
                        {
                            OrbitalConstructionOrientation.AlignIsolated(factory, entityId);
                            Schedule(factory, entityId);
                        }
                    }
                }
            }

            foreach (RingState state in ringStates.Values)
            {
                SynchronizeRingState(state);
            }

            OrbitalRingBraces.Update();
            OrbitalFacilityAttachments.Update();
        }

        private static void SynchronizeRingState(RingState state)
        {
            var data = OrbitalStationManager.Instance.GetPlanetOrbitalRingData(state.Factory.planet.id);
            if (data == null || state.Ring >= data.Rings.Count)
            {
                return;
            }

            EquatorRing ring = data.Rings[state.Ring];
            bool complete;
            if (state.ItemId == ProtoID.I轨道连接组件)
            {
                complete = state.Band == 1 ? ring.isInsideRingComplete : ring.isOutsideRingComplete;
            }
            else if (state.ItemId == ProtoID.I粒子加速轨道)
            {
                complete = ring.isLowInsideRingComplete;
            }
            else
            {
                complete = ring.isLowOutsideRingComplete;
            }

            if (state.Complete == complete)
            {
                return;
            }

            state.Complete = complete;
            OrbitalRingBraces.SetComplete(state.Factory, state.Ring, state.Band, state.ItemId, complete);
        }

        private static float Longitude(Vector3 point)
        {
            return Mathf.Atan2(point.z, point.x) * Mathf.Rad2Deg;
        }

        private static bool IsEntity(PlanetFactory factory, int entityId)
        {
            return factory != null && entityId > 0 && entityId < factory.entityPool.Length
                && factory.entityPool[entityId].id == entityId;
        }

        private static bool IsOrbitalItem(int itemId)
        {
            return itemId == ProtoID.I轨道连接组件 || itemId == ProtoID.I粒子加速轨道 || itemId == ProtoID.I星环电网组件;
        }

        private static void ObserveGame()
        {
            if (!ReferenceEquals(observedGame, GameMain.data))
            {
                OrbitalColliderActivity.Clear();
                Reset();
            }
        }

        internal static void Reset()
        {
            OrbitalFacilityAttachments.Clear();
            OrbitalRingBraces.Clear();
            observedGame = GameMain.data;
            ringStates.Clear();
            scanLoadedFactories = true;
            TrackRenderer.Reset();
        }

        internal static void Dispose()
        {
            OrbitalColliderActivity.Clear();
            OrbitalFacilityAttachments.Dispose();
            OrbitalRingBraces.Dispose();
            if (registered)
            {
                TrackRenderer.ConnectionsChanged -= Schedule;
            }

            OrbitalTrackAppearance.Clear();
            Reset();
            TrackRenderer.Dispose();
            registered = false;
        }
    }
}
