using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    internal sealed class TrackModelTemplate
    {
        internal readonly Vector3[] Vertices;
        internal readonly Vector3[] Normals;
        internal readonly Vector2[] Coordinates;
        internal readonly int[][] Indices;
        internal readonly float ReferenceArcLength;
        internal readonly float ReferenceCoordinateSpan;

        internal TrackModelTemplate(Mesh mesh, float arcLength, float longitudeSpan)
        {
            Vertices = mesh.vertices;
            Normals = mesh.normals;
            Coordinates = mesh.uv;
            Indices = new int[mesh.subMeshCount][];
            for (int surface = 0; surface < Indices.Length; surface++)
            {
                Indices[surface] = mesh.GetTriangles(surface);
            }

            ReferenceArcLength = arcLength;
            ReferenceCoordinateSpan = longitudeSpan / (Mathf.PI * 2) * OrbitalTextureLayout.CoordinatePeriod;
        }
    }

    internal static class TrackModelAssets
    {
        private const string ResourceName = "ProjectOrbitalRing.assets.models.orbital-track-models";
        private static readonly Dictionary<int, TrackModelTemplate> models = new Dictionary<int, TrackModelTemplate>();
        private static readonly Dictionary<string, Texture2D> textures =
            new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly string[] textureNames =
        {
            "orbital-armor-albedo",
            "orbital-structure-deck-albedo-v2",
            "orbital-power-junction-albedo",
            "orbital-power-junction-normal",
            "orbital-power-junction-metal-smoothness",
            "orbital-power-junction-emission",
            "orbital-final-6515-armor-albedo",
            "orbital-final-6515-armor-normal",
            "orbital-final-6515-armor-metallic",
            "orbital-final-6515-armor-emission",
            "orbital-final-6516-armor-albedo",
            "orbital-final-6516-armor-normal",
            "orbital-final-6516-armor-metallic",
            "orbital-final-6516-armor-emission",
            "orbital-final-metal-albedo",
            "orbital-final-metal-normal",
            "orbital-final-metal-indicators",
            "orbital-final-6517-bus-albedo",
            "orbital-final-6517-bus-metallic",
            "orbital-final-6517-bus-normal",
            "orbital-final-6517-bus-current",
            "orbital-final-6516-acceleratorliner-albedo",
            "orbital-final-6516-acceleratorliner-normal",
            "orbital-final-6516-acceleratorliner-indicators",
            "orbital-final-6516-acceleratorliner-workingindicators",
            "orbital-final-6516-particle-beam",
            "orbital-final-6516-particle-beamworking",
        };

        private sealed class Manifest
        {
            public Segment[] Segments { get; set; }
        }

        private sealed class Segment
        {
            public int ItemId { get; set; }
            public int Lod { get; set; }
            public float ReferenceArcLength { get; set; }
            public float ReferenceLongitudeSpanRadians { get; set; }
        }

        internal static TrackModelTemplate Get(int itemId, int detailLevel)
        {
            if (models.Count == 0)
            {
                Initialize();
            }

            return models[itemId * 3 + detailLevel];
        }

        internal static Texture2D GetTexture(string name)
        {
            if (models.Count == 0)
            {
                Initialize();
            }

            return textures[name];
        }

        internal static void Initialize()
        {
            if (models.Count > 0)
            {
                return;
            }

            byte[] data;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (resource == null)
                {
                    throw new InvalidDataException("The orbital track model resource is absent.");
                }

                using (var buffer = new MemoryStream())
                {
                    resource.CopyTo(buffer);
                    data = buffer.ToArray();
                }
            }

            AssetBundle bundle = AssetBundle.LoadFromMemory(data);
            if (bundle == null)
            {
                throw new InvalidDataException("The orbital track model bundle could not be loaded.");
            }

            var importedMeshes = new List<Mesh>();
            TextAsset metadata = null;
            bool retainTextures = false;
            try
            {
                metadata = bundle.LoadAsset<TextAsset>("segments");
                var manifest = metadata == null ? null : JsonConvert.DeserializeObject<Manifest>(metadata.text);
                if (manifest?.Segments == null || manifest.Segments.Length != 9)
                {
                    throw new InvalidDataException("The orbital track model manifest requires nine detail meshes.");
                }

                foreach (Segment segment in manifest.Segments)
                {
                    string name = "track-" + segment.ItemId + "-lod" + segment.Lod;
                    Mesh mesh = bundle.LoadAsset<Mesh>(name);
                    if (mesh != null)
                    {
                        importedMeshes.Add(mesh);
                    }
                    if (mesh == null || segment.ReferenceArcLength <= 0 || segment.ReferenceLongitudeSpanRadians <= 0)
                    {
                        throw new InvalidDataException("Invalid orbital model resource: " + name);
                    }

                    var template = new TrackModelTemplate(
                        mesh, segment.ReferenceArcLength, segment.ReferenceLongitudeSpanRadians);
                    models.Add(segment.ItemId * 3 + segment.Lod, template);
                }

                foreach (string name in textureNames)
                {
                    Texture2D texture = bundle.LoadAsset<Texture2D>(name);
                    if (texture == null)
                    {
                        throw new InvalidDataException("Absent orbital texture resource: " + name);
                    }

                    textures.Add(name, texture);
                }

                if (textures["orbital-final-6516-acceleratorliner-workingindicators"].format != TextureFormat.RGBAHalf)
                {
                    throw new InvalidDataException("Accelerator working indicators require an HDR texture.");
                }

                retainTextures = true;
            }
            catch
            {
                models.Clear();
                textures.Clear();
                throw;
            }
            finally
            {
                bundle.Unload(!retainTextures);
                if (retainTextures)
                {
                    // Mesh templates retain managed channels; materials borrow the centrally owned textures.
                    foreach (Mesh mesh in importedMeshes)
                    {
                        UnityEngine.Object.Destroy(mesh);
                    }

                    if (metadata != null)
                    {
                        UnityEngine.Object.Destroy(metadata);
                    }
                }
            }
        }

        internal static void Dispose()
        {
            models.Clear();
            foreach (Texture2D texture in textures.Values)
            {
                UnityEngine.Object.Destroy(texture);
            }

            textures.Clear();
        }
    }
}
