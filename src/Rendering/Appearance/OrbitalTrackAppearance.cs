using ProjectOrbitalRing.Utils;
using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    internal static class OrbitalTrackAppearance
    {
        internal const float StructureLightIntensity = 1.2f;
        private static Texture2D armorSource;
        internal static Texture2D ArmorTexture => armorSource;
        internal static Material AcceleratorArmor { get; private set; }
        internal static Texture2D AcceleratorStandbyEmission { get; private set; }
        internal static Texture2D AcceleratorWorkingEmission { get; private set; }

        internal static void Configure(TrackMaterialSet materials, int itemId, Color theme)
        {
            LoadArmor();
            var armor = new Material(Shader.Find("Standard"));
            try
            {
                if (itemId == ProtoID.I粒子加速轨道 || itemId == ProtoID.I轨道连接组件)
                {
                    ConfigureArmor(armor, itemId);
                }
                else
                {
                    ConfigureStructuralMetal(armor);
                }

                materials.SetMaterial(TrackSurfaceRole.Fittings, armor);
                if (itemId == ProtoID.I粒子加速轨道)
                {
                    AcceleratorArmor = materials.Fittings;
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(armor);
            }

            if (itemId == ProtoID.I轨道连接组件)
            {
                ConfigureMarkerLights(materials.Core, theme);
                ConfigureStructuralMetal(materials.Detail);
            }
            else if (itemId == ProtoID.I星环电网组件)
            {
                ConfigurePowerConductors(materials.Core);
                ConfigureStructuralMetal(materials.Detail);
            }
            else
            {
                ConfigureBeam(materials);
                ConfigureAcceleratorLiner(materials.Detail);
                materials.Wall.color = new Color(0.88f, 0.94f, 0.98f, 0.16f);
                materials.Wall.SetFloat("_Glossiness", 0.62f);
                materials.Wall.SetFloat("_SmoothMultiplier", 0.72f);
                materials.Wall.SetColor("_SpecularColor", new Color(0.07f, 0.08f, 0.09f));
                materials.Wall.SetColor("_SpecColor", new Color(0.07f, 0.08f, 0.09f));
            }
        }

        private static void LoadArmor()
        {
            if (armorSource != null)
            {
                return;
            }

            armorSource = TrackModelAssets.GetTexture("orbital-armor-albedo");
        }

        private static void ConfigureArmor(Material material, int itemId)
        {
            string prefix = "orbital-final-" + itemId + "-armor-";
            bool accelerator = itemId == ProtoID.I粒子加速轨道;
            material.color = new Color(0.96f, 0.97f, 1);
            material.SetTexture("_MainTex", TrackModelAssets.GetTexture(prefix + "albedo"));
            material.SetTexture("_BumpMap", TrackModelAssets.GetTexture(prefix + "normal"));
            material.SetTexture("_MetallicGlossMap", TrackModelAssets.GetTexture(prefix + "metallic"));
            material.SetTexture("_EmissionMap", TrackModelAssets.GetTexture(prefix + "emission"));
            material.SetFloat("_BumpScale", 0.8f);
            material.SetFloat("_GlossMapScale", accelerator ? 1 : 0.78f);
            material.SetFloat("_SpecularHighlights", 0);
            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.SetColor("_EmissionColor", Color.white);
            material.EnableKeyword("_METALLICGLOSSMAP");
            material.EnableKeyword("_NORMALMAP");
            material.EnableKeyword("_EMISSION");
        }

        private static void ConfigureStructuralMetal(Material material)
        {
            material.shader = Shader.Find("Standard");
            material.color = Color.white;
            material.SetFloat("_Metallic", 0.94f);
            material.SetTexture("_MetallicGlossMap", null);
            material.DisableKeyword("_METALLICGLOSSMAP");
            material.SetFloat("_Glossiness", 0.56f);
            material.SetFloat("_SpecularHighlights", 0);
            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.SetTexture("_MainTex", TrackModelAssets.GetTexture("orbital-final-metal-albedo"));
            material.SetTexture("_BumpMap", TrackModelAssets.GetTexture("orbital-final-metal-normal"));
            material.SetTexture("_EmissionMap", TrackModelAssets.GetTexture("orbital-final-metal-indicators"));
            material.SetColor("_EmissionColor", Color.white);
            material.EnableKeyword("_NORMALMAP");
            material.EnableKeyword("_EMISSION");
        }

        private static void ConfigureMarkerLights(Material material, Color theme)
        {
            material.color = theme * 0.75f;
            material.SetFloat("_Metallic", 0.3f);
            material.SetFloat("_Glossiness", 0.55f);
            material.SetColor("_EmissionColor", theme * StructureLightIntensity);
            material.EnableKeyword("_EMISSION");
        }

        private static void ConfigurePowerConductors(Material material)
        {
            material.color = Color.white;
            material.SetTexture("_MainTex", TrackModelAssets.GetTexture("orbital-final-6517-bus-albedo"));
            material.SetTexture("_MetallicGlossMap", TrackModelAssets.GetTexture("orbital-final-6517-bus-metallic"));
            material.SetTexture("_BumpMap", TrackModelAssets.GetTexture("orbital-final-6517-bus-normal"));
            material.SetTexture("_EmissionMap", TrackModelAssets.GetTexture("orbital-final-6517-bus-current"));
            material.SetFloat("_GlossMapScale", 1);
            material.SetColor("_EmissionColor", Color.white);
            material.EnableKeyword("_METALLICGLOSSMAP");
            material.EnableKeyword("_NORMALMAP");
            material.EnableKeyword("_EMISSION");
        }

        private static void ConfigureBeam(TrackMaterialSet materials)
        {
            Material material = OrbitalParticleBeamMaterial.Create();
            try
            {
                materials.SetMaterial(TrackSurfaceRole.Core, material);
            }
            finally
            {
                UnityEngine.Object.Destroy(material);
            }
        }

        private static void ConfigureAcceleratorLiner(Material material)
        {
            const string prefix = "orbital-final-6516-acceleratorliner-";
            material.shader = Shader.Find("Standard");
            material.color = Color.white;
            material.SetFloat("_Metallic", 0.72f);
            material.SetFloat("_Glossiness", 0.76f);
            material.SetTexture("_MainTex", TrackModelAssets.GetTexture(prefix + "albedo"));
            material.SetTexture("_BumpMap", TrackModelAssets.GetTexture(prefix + "normal"));
            AcceleratorStandbyEmission = TrackModelAssets.GetTexture(prefix + "indicators");
            AcceleratorWorkingEmission = TrackModelAssets.GetTexture(prefix + "workingindicators");
            material.SetTexture("_EmissionMap", AcceleratorStandbyEmission);
            material.SetColor("_EmissionColor", Color.white);
            material.EnableKeyword("_NORMALMAP");
            material.EnableKeyword("_EMISSION");
        }

        internal static void Clear()
        {
            armorSource = null;
            AcceleratorArmor = null;
            AcceleratorStandbyEmission = null;
            AcceleratorWorkingEmission = null;
            OrbitalParticleBeamMaterial.Clear();
        }
    }
}
