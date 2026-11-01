using System;
using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalParticleBeamMaterial
    {
        internal static Texture2D StandbyTexture { get; private set; }
        internal static Texture2D WorkingTexture { get; private set; }

        internal static Material Create()
        {
            Shader shader = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive");
            if (shader == null || !shader.isSupported)
            {
                throw new InvalidOperationException("The additive particle shader is unavailable.");
            }

            var material = new Material(shader)
            {
                name = "OrbitalRing.Particle.Beam",
                renderQueue = 3005,
            };
            try
            {
                StandbyTexture = TrackModelAssets.GetTexture("orbital-final-6516-particle-beam");
                WorkingTexture = TrackModelAssets.GetTexture("orbital-final-6516-particle-beamworking");
                material.SetTexture("_MainTex", StandbyTexture);
                material.SetColor("_TintColor", Color.white);
                material.DisableKeyword("SOFTPARTICLES_ON");
                return material;
            }
            catch
            {
                UnityEngine.Object.Destroy(material);
                throw;
            }
        }

        internal static void Clear()
        {
            StandbyTexture = null;
            WorkingTexture = null;
        }
    }
}
