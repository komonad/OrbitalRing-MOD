using System;
using System.Collections.Generic;
using System.Diagnostics;
using ProjectOrbitalRing.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using static ProjectOrbitalRing.Patches.Logic.OrbitalRing.PosTool;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalRingBraces
    {
        private sealed class RingPair
        {
            internal PlanetFactory Factory;
            internal int Ring;
            internal int Band;
            internal bool StructureComplete;
            internal bool FunctionalComplete;
            internal Mesh Mesh;
            internal Mesh Lights;
            internal Mesh[] VisibleMeshes;
            internal Mesh[] VisibleLights;
            internal int Anchors;
            internal int Revision;
            internal BraceBuild Pending;
        }

        private sealed class BraceBuild
        {
            internal RingPair Pair;
            internal int Revision;
            internal Mesh Mesh;
            internal Mesh Lights;
            internal readonly List<Mesh> VisibleMeshes = new List<Mesh>();
            internal readonly List<Mesh> VisibleLights = new List<Mesh>();
            internal int Anchors;
            internal IEnumerator<object> Work;
            internal LinkedListNode<BraceBuild> QueueNode;
        }

        private static readonly Dictionary<long, RingPair> pairs = new Dictionary<long, RingPair>();
        private static readonly LinkedList<BraceBuild> buildQueue = new LinkedList<BraceBuild>();
        private static Material metal;
        private static Material signal;
        private static readonly MaterialPropertyBlock lighting = new MaterialPropertyBlock();
        private static readonly MaterialPropertyBlock signalProperties = new MaterialPropertyBlock();
        private static readonly List<Mesh> drawMeshes = new List<Mesh>();
        internal static int ActivePairs { get; private set; }
        internal static int PendingBuildCount => buildQueue.Count;

        internal static void SetComplete(PlanetFactory factory, int ring, int band, int itemId, bool complete)
        {
            long key = ((long)factory.planetId << 32) | (uint)(ring * 4 + band);
            if (!pairs.TryGetValue(key, out var pair))
            {
                pair = new RingPair
                {
                    Factory = factory,
                    Ring = ring,
                    Band = band,
                };
                pairs.Add(key, pair);
            }
            else if (!ReferenceEquals(pair.Factory, factory))
            {
                CancelBuild(pair);
                ReleasePair(pair);
                pair.Factory = factory;
                pair.StructureComplete = false;
                pair.FunctionalComplete = false;
            }

            bool previousStructure = pair.StructureComplete;
            bool previousFunctional = pair.FunctionalComplete;
            if (itemId == ProtoID.I轨道连接组件)
            {
                pair.StructureComplete = complete;
            }
            else if (itemId == (band == 1 ? ProtoID.I粒子加速轨道 : ProtoID.I星环电网组件))
            {
                pair.FunctionalComplete = complete;
            }

            if (previousStructure != pair.StructureComplete || previousFunctional != pair.FunctionalComplete)
            {
                pair.Revision++;
                CancelBuild(pair);
            }

            bool active = pair.StructureComplete && pair.FunctionalComplete;
            if (active && pair.Mesh == null && pair.Pending == null)
            {
                var candidate = new BraceBuild { Pair = pair, Revision = pair.Revision };
                candidate.Work = Build(candidate).GetEnumerator();
                candidate.QueueNode = buildQueue.AddLast(candidate);
                pair.Pending = candidate;
            }
            else if (!active)
            {
                ReleasePair(pair);
            }
        }

        internal static void Update()
        {
            if (buildQueue.Count == 0)
            {
                return;
            }

            long frameStarted = Stopwatch.GetTimestamp();
            while (buildQueue.Count > 0 && OrbitalMeshChunks.HasBuildBudget &&
                Stopwatch.GetTimestamp() - frameStarted < Stopwatch.Frequency * 0.001)
            {
                var candidate = buildQueue.First.Value;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    if (!candidate.Work.MoveNext())
                    {
                        if (candidate.Revision == candidate.Pair.Revision && candidate.Pair.StructureComplete &&
                            candidate.Pair.FunctionalComplete)
                        {
                            Publish(candidate);
                        }

                        CancelBuild(candidate.Pair);
                    }
                }
                catch
                {
                    CancelBuild(candidate.Pair);
                    throw;
                }
                finally
                {
                    OrbitalMeshChunks.RecordBuildTime(started);
                }
            }
        }

        private static void Publish(BraceBuild candidate)
        {
            var pair = candidate.Pair;
            Mesh[] visibleMeshes = candidate.VisibleMeshes.ToArray();
            Mesh[] visibleLights = candidate.VisibleLights.ToArray();
            ReleasePair(pair);
            pair.Mesh = candidate.Mesh;
            pair.Lights = candidate.Lights;
            pair.VisibleMeshes = visibleMeshes;
            pair.VisibleLights = visibleLights;
            pair.Anchors = candidate.Anchors;
            candidate.Mesh = null;
            candidate.Lights = null;
            candidate.VisibleMeshes.Clear();
            candidate.VisibleLights.Clear();
            drawMeshes.Capacity = Math.Max(drawMeshes.Capacity,
                Math.Max(pair.VisibleMeshes.Length, pair.VisibleLights.Length));
            ActivePairs++;
        }

        private static void CancelBuild(RingPair pair)
        {
            var candidate = pair.Pending;
            if (candidate == null)
            {
                return;
            }

            pair.Pending = null;
            if (candidate.QueueNode?.List != null)
            {
                buildQueue.Remove(candidate.QueueNode);
            }

            candidate.Work.Dispose();
            OrbitalMeshChunks.Destroy(candidate.VisibleMeshes);
            OrbitalMeshChunks.Destroy(candidate.VisibleLights);
            candidate.VisibleMeshes.Clear();
            candidate.VisibleLights.Clear();
            if (candidate.Mesh != null)
            {
                UnityEngine.Object.Destroy(candidate.Mesh);
                candidate.Mesh = null;
            }
            if (candidate.Lights != null)
            {
                UnityEngine.Object.Destroy(candidate.Lights);
                candidate.Lights = null;
            }
        }

        private static void ReleasePair(RingPair pair)
        {
            OrbitalMeshChunks.Destroy(pair.VisibleMeshes);
            OrbitalMeshChunks.Destroy(pair.VisibleLights);
            pair.VisibleMeshes = null;
            pair.VisibleLights = null;
            if (pair.Mesh != null)
            {
                UnityEngine.Object.Destroy(pair.Mesh);
                pair.Mesh = null;
                ActivePairs--;
            }
            if (pair.Lights != null)
            {
                UnityEngine.Object.Destroy(pair.Lights);
                pair.Lights = null;
            }

            pair.Anchors = 0;
        }

        internal static void Draw(PlanetFactory factory, Camera camera)
        {
            var model = factory?.planet?.factoryModel;
            if (camera == null || model == null || model.disableFactoryEntities
                || model.gpuiManager == null || !model.gpuiManager.renderEntity)
            {
                return;
            }

            OrbitalRenderBounds.Prepare(camera);
            TrackRendering.ApplyMetalLighting(lighting, Vector3.up);
            foreach (var pair in pairs.Values)
            {
                if (pair.Factory != factory || pair.Mesh == null)
                {
                    continue;
                }

                if (pair.Mesh != null)
                {
                    DrawVisible(pair.Mesh, pair.VisibleMeshes, Metal, lighting, factory, camera, true);
                }

                if (pair.Lights != null)
                {
                    Color theme = pair.Band == 1 ? new Color(0.64f, 0.36f, 0.95f) : new Color(0.10f, 0.90f, 0.28f);
                    float breath = 1.1f + Mathf.Sin(Time.time * Mathf.PI * 2 / 3 + pair.Ring * 0.4f) * 0.30f;
                    signalProperties.SetColor("_Color", Color.Lerp(theme, Color.white, 0.35f) * 0.4f);
                    signalProperties.SetColor("_EmissionColor", theme * breath);
                    DrawVisible(pair.Lights, pair.VisibleLights, Signal, signalProperties, factory, camera, false);
                }
            }
        }

        private static void DrawVisible(Mesh source, Mesh[] meshes, Material material,
            MaterialPropertyBlock properties, PlanetFactory factory, Camera camera, bool receiveShadows)
        {
            drawMeshes.Clear();
            foreach (Mesh mesh in meshes)
            {
                if (OrbitalRenderBounds.IsVisible(camera, mesh.bounds, factory.planet.realRadius - 2))
                {
                    drawMeshes.Add(mesh);
                }
            }

            bool useChunks = drawMeshes.Count * 3 <= meshes.Length;
            if (receiveShadows || !useChunks)
            {
                ShadowCastingMode casting = receiveShadows
                    ? (useChunks ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On) : ShadowCastingMode.Off;
                Graphics.DrawMesh(source, Matrix4x4.identity, material, 0, camera, 0, properties,
                    casting, receiveShadows && !useChunks);
            }

            if (!useChunks)
            {
                return;
            }

            foreach (Mesh mesh in drawMeshes)
            {
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, camera, 0, properties,
                    ShadowCastingMode.Off, receiveShadows);
            }
        }

        internal static Material Metal
        {
            get
            {
                if (metal == null)
                {
                    metal = new Material(Shader.Find("Standard"))
                    {
                        name = "OrbitalRing.BraceMetal",
                    };
                    metal.color = new Color(0.76f, 0.80f, 0.86f);
                    metal.SetFloat("_Metallic", 0.88f);
                    metal.SetFloat("_Glossiness", 0.60f);
                    metal.SetFloat("_SpecularHighlights", 0);
                    metal.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                    metal.SetColor("_EmissionColor", new Color(0.025f, 0.028f, 0.032f));
                    metal.EnableKeyword("_EMISSION");
                }

                return metal;
            }
        }

        private static Material Signal
        {
            get
            {
                if (signal == null)
                {
                    signal = new Material(Shader.Find("Standard"))
                    {
                        name = "OrbitalRing.BraceIndicators",
                    };
                    signal.SetFloat("_Metallic", 0.05f);
                    signal.SetFloat("_Glossiness", 0.60f);
                    signal.EnableKeyword("_EMISSION");
                }

                return signal;
            }
        }

        private static Vector3 Center(RingPair pair, float longitude, bool structure)
        {
            int itemId = structure ? ProtoID.I轨道连接组件 : pair.Band == 1 ? ProtoID.I粒子加速轨道 : ProtoID.I星环电网组件;
            return OrbitalTextureLayout.TrackCenter(pair.Factory, pair.Ring, pair.Band, longitude, itemId);
        }

        private static IEnumerable<object> Build(BraceBuild candidate)
        {
            var pair = candidate.Pair;
            bool accelerator = pair.Band == 1;
            int functionalItem = accelerator ? ProtoID.I粒子加速轨道 : ProtoID.I星环电网组件;
            var layout = OrbitalTextureLayout.FromBand(pair.Factory.planet, functionalItem, pair.Band,
                accelerator ? 1.28f : 0.98f);
            int anchors = Math.Max(1, Mathf.RoundToInt(layout.RepeatCount / (accelerator ? 10f : 5f)));
            candidate.Anchors = anchors;
            var builder = new OrbitalAttachmentMesh();
            var indicators = new OrbitalAttachmentMesh();
            for (int index = 0; index < anchors; index++)
            {
                float longitude = OrbitalTextureLayout.FacilityLongitude(pair.Factory.planet,
                    index * (accelerator ? 1f : 0.5f));
                Vector3 upper = Center(pair, longitude, true);
                Vector3 lower = Center(pair, longitude, false);
                Vector3 up = upper.normalized;
                Vector3 tangent = Vector3.Cross(Vector3.up, up).normalized;
                Vector3 side = Vector3.Cross(tangent, up).normalized;
                foreach (float sign in new[] { -1f, 1f })
                {
                    float lateral = accelerator ? 1.29f : 0.64f;
                    Vector3 top = upper - up * (accelerator ? 0.80f : 0.655f) + side * (sign * lateral);
                    Vector3 bottom = lower + up * (accelerator ? 0 : 0.574f) + side * (sign * lateral);
                    AddMount(builder, top, -up, side, tangent);
                    AddMount(builder, bottom, accelerator ? side * sign : up, accelerator ? up : side, tangent);
                    builder.Cylinder(top, bottom, 0.042f);
                    Vector3 axis = (bottom - top).normalized;
                    Vector3 sleeve = Vector3.Lerp(top, bottom, 0.34f);
                    builder.Cylinder(sleeve - axis * 0.18f, sleeve + axis * 0.18f, 0.068f);
                    builder.Cylinder(top + axis * 0.09f, top + axis * 0.21f, 0.085f);
                    builder.Cylinder(bottom - axis * 0.21f, bottom - axis * 0.09f, 0.085f);
                    indicators.Cylinder(top + axis * 0.22f, top + axis * 0.29f, 0.070f);
                    indicators.Cylinder(bottom - axis * 0.29f, bottom - axis * 0.22f, 0.070f);
                }

                yield return null;
            }

            candidate.Lights = indicators.Build();
            candidate.Lights.name = "OrbitalRing.BraceIndicators";
            yield return null;
            candidate.Mesh = builder.Build();
            yield return null;
            foreach (var step in OrbitalMeshChunks.Create(candidate.Mesh, candidate.VisibleMeshes))
            {
                yield return step;
            }
            foreach (var step in OrbitalMeshChunks.Create(candidate.Lights, candidate.VisibleLights))
            {
                yield return step;
            }
        }

        private static void AddMount(OrbitalAttachmentMesh builder, Vector3 anchor, Vector3 normal,
            Vector3 across, Vector3 along)
        {
            builder.Box(anchor + normal * 0.035f, across, normal, along, new Vector3(0.32f, 0.07f, 0.42f));
            builder.Box(anchor + normal * 0.10f, across, normal, along, new Vector3(0.21f, 0.06f, 0.25f));
            foreach (float offset in new[] { -0.145f, 0.145f })
            {
                Vector3 bolt = anchor + along * offset + normal * 0.07f;
                builder.Cylinder(bolt, bolt + normal * 0.035f, 0.026f);
            }
        }

        internal static void Clear()
        {
            foreach (var pair in pairs.Values)
            {
                CancelBuild(pair);
                ReleasePair(pair);
            }

            buildQueue.Clear();
            pairs.Clear();
            ActivePairs = 0;
        }

        internal static void Dispose()
        {
            Clear();
            if (metal != null)
            {
                UnityEngine.Object.Destroy(metal);
                metal = null;
            }

            if (signal != null)
            {
                UnityEngine.Object.Destroy(signal);
                signal = null;
            }
        }
    }
}
