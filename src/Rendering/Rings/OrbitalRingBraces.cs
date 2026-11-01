using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using ProjectOrbitalRing.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using static ProjectOrbitalRing.Patches.Logic.OrbitalRing.PosTool;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalRingBraces
    {
        private const int DetailCount = 3;
        private const int AnchorsPerChunk = 4;

        private sealed class BraceDetail
        {
            internal Mesh Mesh;
            internal Mesh Lights;
        }

        private sealed class BraceChunk
        {
            internal readonly BraceDetail[] Details = CreateDetails();
            internal Bounds Bounds;
            internal int DetailLevel = 2;
        }

        private readonly struct MountSurface
        {
            internal readonly Vector3 Center;
            internal readonly Vector3 Normal;
            internal readonly Vector3 Across;
            internal readonly Vector3 Along;

            internal MountSurface(Vector3 center, Vector3 normal, Vector3 across, Vector3 along)
            {
                Center = center;
                Normal = normal;
                Across = across;
                Along = along;
            }
        }

        private sealed class RingPair
        {
            internal PlanetFactory Factory;
            internal int Ring;
            internal int Band;
            internal bool StructureComplete;
            internal bool FunctionalComplete;
            internal BraceDetail[] Details;
            internal BraceChunk[] Chunks;
            internal int Anchors;
            internal int Revision;
            internal BraceBuild Pending;
        }

        private sealed class BraceBuild
        {
            internal RingPair Pair;
            internal int Revision;
            internal BraceDetail[] Details = CreateDetails();
            internal readonly List<BraceChunk> Chunks = new List<BraceChunk>();
            internal int Anchors;
            internal IEnumerator<object> Work;
            internal LinkedListNode<BraceBuild> QueueNode;
        }

        private static readonly Dictionary<long, RingPair> pairs = new Dictionary<long, RingPair>();
        private static readonly LinkedList<BraceBuild> buildQueue = new LinkedList<BraceBuild>();
        private static Material metal;
        private static Material signal;
        private static Material simplifiedMetal;
        private static Texture2D mountNormal;
        private static readonly MaterialPropertyBlock lighting = new MaterialPropertyBlock();
        private static readonly MaterialPropertyBlock signalProperties = new MaterialPropertyBlock();
        private static readonly List<BraceChunk> visibleChunks = new List<BraceChunk>();
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
            if (active && pair.Details == null && pair.Pending == null)
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
            BraceChunk[] chunks = candidate.Chunks.ToArray();
            ReleasePair(pair);
            pair.Details = candidate.Details;
            candidate.Details = null;
            pair.Chunks = chunks;
            candidate.Chunks.Clear();
            pair.Anchors = candidate.Anchors;
            visibleChunks.Capacity = Math.Max(visibleChunks.Capacity, pair.Chunks.Length);
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
            ReleaseDetails(candidate.Details);
            foreach (BraceChunk chunk in candidate.Chunks)
            {
                ReleaseDetails(chunk.Details);
            }
            candidate.Chunks.Clear();
        }

        private static void ReleasePair(RingPair pair)
        {
            bool active = pair.Details != null;
            ReleaseDetails(pair.Details);
            pair.Details = null;
            if (pair.Chunks != null)
            {
                foreach (BraceChunk chunk in pair.Chunks)
                {
                    ReleaseDetails(chunk.Details);
                }
                pair.Chunks = null;
            }

            if (active)
            {
                ActivePairs--;
            }
            pair.Anchors = 0;
        }

        private static BraceDetail[] CreateDetails()
        {
            var details = new BraceDetail[DetailCount];
            for (int detailLevel = 0; detailLevel < details.Length; detailLevel++)
            {
                details[detailLevel] = new BraceDetail();
            }
            return details;
        }

        private static void ReleaseDetails(BraceDetail[] details)
        {
            if (details == null)
            {
                return;
            }

            foreach (BraceDetail detail in details)
            {
                if (detail.Mesh != null)
                {
                    UnityEngine.Object.Destroy(detail.Mesh);
                    detail.Mesh = null;
                }
                if (detail.Lights != null)
                {
                    UnityEngine.Object.Destroy(detail.Lights);
                    detail.Lights = null;
                }
            }
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
                if (pair.Factory != factory || pair.Details == null)
                {
                    continue;
                }

                Color theme = pair.Band == 1 ? new Color(0.64f, 0.36f, 0.95f) : new Color(0.10f, 0.90f, 0.28f);
                float breath = 1.1f + Mathf.Sin(Time.time * Mathf.PI * 2 / 3 + pair.Ring * 0.4f) * 0.30f;
                signalProperties.SetColor("_Color", Color.Lerp(theme, Color.white, 0.35f) * 0.4f);
                signalProperties.SetColor("_EmissionColor", theme * breath);
                DrawPair(pair, camera);
            }
        }

        private static void DrawPair(RingPair pair, Camera camera)
        {
            visibleChunks.Clear();
            int commonDetail = -1;
            bool uniformDetail = true;
            Vector3 cameraPosition = camera.transform.position;
            float screenScale = camera.orthographic ? camera.pixelHeight / (2 * camera.orthographicSize)
                : camera.pixelHeight / (2 * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f));
            foreach (BraceChunk chunk in pair.Chunks)
            {
                float distance = Mathf.Sqrt(Mathf.Max(1, chunk.Bounds.SqrDistance(cameraPosition)));
                float collarPixels = 0.085f * screenScale / (camera.orthographic ? 1 : distance);
                chunk.DetailLevel = DetailLevel(collarPixels, chunk.DetailLevel);
                if (OrbitalRenderBounds.IsVisible(camera, chunk.Bounds, pair.Factory.planet.realRadius - 2))
                {
                    if (commonDetail < 0)
                    {
                        commonDetail = chunk.DetailLevel;
                    }
                    uniformDetail &= commonDetail == chunk.DetailLevel;
                    visibleChunks.Add(chunk);
                }
            }

            // A fixed caster keeps ground shadows independent of view culling and detail transitions.
            Graphics.DrawMesh(pair.Details[2].Mesh, Matrix4x4.identity, Metal, 0, camera, 0, lighting,
                ShadowCastingMode.ShadowsOnly, false);
            if (uniformDetail && visibleChunks.Count * 3 > pair.Chunks.Length)
            {
                DrawDetail(pair.Details[commonDetail], commonDetail, camera);
                return;
            }

            foreach (BraceChunk chunk in visibleChunks)
            {
                DrawDetail(chunk.Details[chunk.DetailLevel], chunk.DetailLevel, camera);
            }
        }

        private static int DetailLevel(float collarPixels, int previous)
        {
            if (collarPixels > (previous == 0 ? 2.5f : 3.5f))
            {
                return 0;
            }
            return collarPixels > (previous == 2 ? 1.1f : 0.75f) ? 1 : 2;
        }

        private static void DrawDetail(BraceDetail detail, int detailLevel, Camera camera)
        {
            Graphics.DrawMesh(detail.Mesh, Matrix4x4.identity, detailLevel == 0 ? Metal : SimplifiedMetal,
                0, camera, 0, lighting, ShadowCastingMode.Off, true);
            Graphics.DrawMesh(detail.Lights, Matrix4x4.identity, Signal, 0, camera, 0, signalProperties,
                ShadowCastingMode.Off, false);
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

        private static Material SimplifiedMetal
        {
            get
            {
                if (simplifiedMetal != null)
                {
                    return simplifiedMetal;
                }

                const string resourceName = "ProjectOrbitalRing.assets.texture.orbital-brace-mount-normal.png";
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                using (var buffer = new MemoryStream())
                {
                    if (stream == null)
                    {
                        throw new InvalidDataException("The brace mounting normal resource is absent.");
                    }
                    stream.CopyTo(buffer);
                    mountNormal = new Texture2D(2, 2, TextureFormat.RGBA32, true, true)
                    {
                        name = "OrbitalRing.BraceMountNormal",
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Trilinear,
                    };
                    if (!mountNormal.LoadImage(buffer.ToArray()))
                    {
                        UnityEngine.Object.Destroy(mountNormal);
                        mountNormal = null;
                        throw new InvalidDataException("The brace mounting normal resource could not be decoded.");
                    }
                    mountNormal.Apply(true, true);
                }

                simplifiedMetal = new Material(Metal) { name = "OrbitalRing.BraceMetal.Simplified" };
                simplifiedMetal.SetTexture("_BumpMap", mountNormal);
                simplifiedMetal.SetFloat("_BumpScale", 0.8f);
                simplifiedMetal.EnableKeyword("_NORMALMAP");
                return simplifiedMetal;
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
            for (int firstAnchor = 0; firstAnchor < anchors; firstAnchor += AnchorsPerChunk)
            {
                var chunk = new BraceChunk();
                candidate.Chunks.Add(chunk);
                for (int detailLevel = 0; detailLevel < DetailCount; detailLevel++)
                {
                    var builder = new OrbitalAttachmentMesh();
                    var indicators = new OrbitalAttachmentMesh();
                    var mountingSurfaces = new List<MountSurface>();
                    int lastAnchor = Math.Min(anchors, firstAnchor + AnchorsPerChunk);
                    for (int index = firstAnchor; index < lastAnchor; index++)
                    {
                        AddAnchor(builder, indicators, mountingSurfaces, pair, index, detailLevel);
                        yield return null;
                    }

                    BraceDetail detail = chunk.Details[detailLevel];
                    detail.Mesh = builder.Build();
                    detail.Mesh.name = "OrbitalRing.BraceMetal.Lod" + detailLevel;
                    if (detailLevel > 0)
                    {
                        ApplyMountNormals(detail.Mesh, mountingSurfaces);
                    }
                    detail.Lights = indicators.Build();
                    detail.Lights.name = "OrbitalRing.BraceIndicators.Lod" + detailLevel;
                    if (detailLevel == 0)
                    {
                        chunk.Bounds = detail.Mesh.bounds;
                        chunk.Bounds.Encapsulate(detail.Lights.bounds);
                    }
                    yield return null;
                }
            }

            for (int detailLevel = 0; detailLevel < DetailCount; detailLevel++)
            {
                candidate.Details[detailLevel].Mesh = Combine(candidate.Chunks, detailLevel, false);
                yield return null;
                candidate.Details[detailLevel].Lights = Combine(candidate.Chunks, detailLevel, true);
                yield return null;
                candidate.Details[detailLevel].Mesh.UploadMeshData(true);
                candidate.Details[detailLevel].Lights.UploadMeshData(true);
                foreach (BraceChunk chunk in candidate.Chunks)
                {
                    chunk.Details[detailLevel].Mesh.UploadMeshData(true);
                    chunk.Details[detailLevel].Lights.UploadMeshData(true);
                    yield return null;
                }
            }
        }

        private static Mesh Combine(List<BraceChunk> chunks, int detailLevel, bool lights)
        {
            var instances = new CombineInstance[chunks.Count];
            for (int index = 0; index < instances.Length; index++)
            {
                BraceDetail detail = chunks[index].Details[detailLevel];
                instances[index].mesh = lights ? detail.Lights : detail.Mesh;
            }
            var mesh = new Mesh
            {
                name = "OrbitalRing.Braces.Lod" + detailLevel + (lights ? ".Lights" : ".Metal"),
                indexFormat = IndexFormat.UInt32,
            };
            try
            {
                mesh.CombineMeshes(instances, true, false);
                return mesh;
            }
            catch
            {
                UnityEngine.Object.Destroy(mesh);
                throw;
            }
        }

        private static void AddAnchor(OrbitalAttachmentMesh builder, OrbitalAttachmentMesh indicators,
            List<MountSurface> mountingSurfaces, RingPair pair, int index, int detailLevel)
        {
            bool accelerator = pair.Band == 1;
            float longitude = OrbitalTextureLayout.FacilityLongitude(pair.Factory.planet,
                index * (accelerator ? 1f : 0.5f));
            Vector3 upper = Center(pair, longitude, true);
            Vector3 lower = Center(pair, longitude, false);
            Vector3 up = upper.normalized;
            Vector3 tangent = Vector3.Cross(Vector3.up, up).normalized;
            Vector3 side = Vector3.Cross(tangent, up).normalized;
            int segments = detailLevel == 0 ? 12 : detailLevel == 1 ? 8 : 6;
            for (int sideIndex = 0; sideIndex < 2; sideIndex++)
            {
                float sign = sideIndex == 0 ? -1 : 1;
                float lateral = accelerator ? 1.29f : 0.64f;
                Vector3 top = upper - up * (accelerator ? 0.80f : 0.655f) + side * (sign * lateral);
                Vector3 bottom = lower + up * (accelerator ? 0 : 0.574f) + side * (sign * lateral);
                AddMount(builder, mountingSurfaces, top, -up, side, tangent, detailLevel);
                AddMount(builder, mountingSurfaces, bottom, accelerator ? side * sign : up,
                    accelerator ? up : side, tangent, detailLevel);
                AddCylinder(builder, top, bottom, 0.042f, segments);
                Vector3 axis = (bottom - top).normalized;
                Vector3 sleeve = Vector3.Lerp(top, bottom, 0.34f);
                AddCylinder(builder, sleeve - axis * 0.18f, sleeve + axis * 0.18f, 0.068f, segments);
                AddCylinder(builder, top + axis * 0.09f, top + axis * 0.21f, 0.085f, segments);
                AddCylinder(builder, bottom - axis * 0.21f, bottom - axis * 0.09f, 0.085f, segments);
                AddCylinder(indicators, top + axis * 0.22f, top + axis * 0.29f, 0.070f,
                    detailLevel == 0 ? 12 : detailLevel == 1 ? 6 : 4);
                AddCylinder(indicators, bottom - axis * 0.29f, bottom - axis * 0.22f, 0.070f,
                    detailLevel == 0 ? 12 : detailLevel == 1 ? 6 : 4);
            }
        }

        private static void AddCylinder(OrbitalAttachmentMesh builder, Vector3 first, Vector3 last,
            float radius, int segments)
        {
            Vector3 axis = (last - first).normalized;
            Vector3 up = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            builder.Tube((first + last) * 0.5f, axis, up, 0, radius, Vector3.Distance(first, last), segments);
        }

        private static void AddMount(OrbitalAttachmentMesh builder, List<MountSurface> mountingSurfaces,
            Vector3 anchor, Vector3 normal, Vector3 across, Vector3 along, int detailLevel)
        {
            float surfaceHeight = detailLevel == 2 ? 0.13f : 0.07f;
            builder.Box(anchor + normal * (surfaceHeight * 0.5f), across, normal, along,
                new Vector3(0.32f, surfaceHeight, 0.42f));
            if (detailLevel < 2)
            {
                builder.Box(anchor + normal * 0.10f, across, normal, along, new Vector3(0.21f, 0.06f, 0.25f));
            }
            if (detailLevel > 0)
            {
                mountingSurfaces.Add(new MountSurface(anchor + normal * surfaceHeight, normal, across, along));
                return;
            }

            foreach (float offset in new[] { -0.145f, 0.145f })
            {
                Vector3 bolt = anchor + along * offset + normal * 0.07f;
                builder.Cylinder(bolt, bolt + normal * 0.035f, 0.026f);
            }
        }

        private static void ApplyMountNormals(Mesh mesh, List<MountSurface> mountingSurfaces)
        {
            Vector3[] positions = mesh.vertices;
            Vector3[] normals = mesh.normals;
            var coordinates = new Vector2[positions.Length];
            var tangents = new Vector4[positions.Length];
            for (int index = 0; index < positions.Length; index++)
            {
                Vector3 normal = normals[index];
                Vector3 tangent = Vector3.Cross(Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right, normal);
                tangent.Normalize();
                tangents[index] = new Vector4(tangent.x, tangent.y, tangent.z, 1);
                foreach (MountSurface surface in mountingSurfaces)
                {
                    if (Vector3.Dot(normal, surface.Normal) < 0.999f)
                    {
                        continue;
                    }
                    Vector3 offset = positions[index] - surface.Center;
                    float horizontal = Vector3.Dot(offset, surface.Across);
                    float vertical = Vector3.Dot(offset, surface.Along);
                    if (Mathf.Abs(Vector3.Dot(offset, surface.Normal)) > 0.0001f ||
                        Mathf.Abs(horizontal) > 0.1601f || Mathf.Abs(vertical) > 0.2101f)
                    {
                        continue;
                    }
                    coordinates[index] = new Vector2(horizontal / 0.32f + 0.5f, vertical / 0.42f + 0.5f);
                    Vector3 across = surface.Across;
                    float handedness = Vector3.Dot(Vector3.Cross(normal, across), surface.Along) >= 0 ? 1 : -1;
                    tangents[index] = new Vector4(across.x, across.y, across.z, handedness);
                    break;
                }
            }
            mesh.uv = coordinates;
            mesh.tangents = tangents;
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
            visibleChunks.Clear();
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
            if (simplifiedMetal != null)
            {
                UnityEngine.Object.Destroy(simplifiedMetal);
                simplifiedMetal = null;
            }
            if (mountNormal != null)
            {
                UnityEngine.Object.Destroy(mountNormal);
                mountNormal = null;
            }
        }
    }
}
