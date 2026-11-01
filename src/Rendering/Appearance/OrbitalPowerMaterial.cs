using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalPowerMaterial
    {
        internal static Material Create()
        {
            var material = new Material(OrbitalRingBraces.Metal)
            {
                name = "OrbitalRing.Facility.PowerPanel",
                color = Color.white,
            };
            material.mainTexture = TrackModelAssets.GetTexture("orbital-power-junction-albedo");
            material.SetTexture("_BumpMap", TrackModelAssets.GetTexture("orbital-power-junction-normal"));
            material.SetTexture("_MetallicGlossMap",
                TrackModelAssets.GetTexture("orbital-power-junction-metal-smoothness"));
            material.SetTexture("_EmissionMap", TrackModelAssets.GetTexture("orbital-power-junction-emission"));
            material.SetFloat("_BumpScale", 0.85f);
            material.SetFloat("_GlossMapScale", 1);
            material.SetColor("_EmissionColor", Color.white);
            material.EnableKeyword("_NORMALMAP");
            material.EnableKeyword("_METALLICGLOSSMAP");
            material.EnableKeyword("_EMISSION");
            return material;
        }
    }
}
