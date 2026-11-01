using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectOrbitalRing.Rendering
{
    internal static class TrackMaterialDefaults
    {
        private static Material body;
        private static Material joint;
        private static Material detail;
        private static Material innerBody;
        public static Material JointMaterial
        {
            get
            {
                var material = BodyMaterial;
                return joint;
            }
        }

        internal static Material InnerWallMaterial
        {
            get
            {
                var initialized = BodyMaterial;
                return innerBody;
            }
        }

        internal static Material DetailMaterial
        {
            get
            {
                var initialized = BodyMaterial;
                return detail;
            }
        }

        public static Material BodyMaterial
        {
            get
            {
                if (body == null)
                {
                    var shader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
                    if (shader == null)
                    {
                        throw new InvalidOperationException("Track surface shader is unavailable.");
                    }

                    var glass = TankGlassMaterial();
                    var glassShader = Shader.Find("VF Shaders/Forward/PBR Standard Glass")
                        ?? Shader.Find("Standard (Specular setup)") ?? shader;
                    body = new Material(glassShader);
                    body.name = "OrbitalRing.GlassWall";
                    body.color = new Color(0.90f, 0.96f, 0.98f, 0.22f);
                    body.SetTexture("_MainTex", Texture2D.whiteTexture);
                    Transparent(body, 3000);
                    if (body.HasProperty("_SpecColor"))
                    {
                        Color specular = glass?.GetColor("_SpecularColor") ?? new Color(0.198f, 0.198f, 0.198f);
                        body.SetColor("_SpecColor", specular);
                    }

                    if (body.HasProperty("_RimColor"))
                    {
                        var rimColor = glass?.GetColor("_RimColor") ?? new Color(0.604f, 0.846f, 0.859f);
                        rimColor.a = 0.3f;
                        body.SetColor("_RimColor", rimColor);
                    }

                    if (body.HasProperty("_SpecularColor"))
                    {
                        body.SetColor("_SpecularColor", new Color(0.26f, 0.27f, 0.28f));
                    }

                    if (body.HasProperty("_MetallicMultiplier"))
                    {
                        body.SetFloat("_MetallicMultiplier", 0);
                    }

                    if (body.HasProperty("_SmoothMultiplier"))
                    {
                        body.SetFloat("_SmoothMultiplier", 0.94f);
                    }

                    if (body.HasProperty("_MS_Tex"))
                    {
                        body.SetTexture("_MS_Tex", Texture2D.whiteTexture);
                    }

                    joint = new Material(shader);
                    joint.name = "OrbitalRing.Coupling";
                    joint.color = new Color(0.82f, 0.84f, 0.86f);
                    detail = new Material(shader)
                    {
                        name = "OrbitalRing.Detail",
                        color = new Color(0.27f, 0.31f, 0.34f),
                    };
                    detail.SetFloat("_Metallic", 0.12f);
                    detail.SetFloat("_Glossiness", 0.38f);
                    if (body.HasProperty("_Metallic"))
                    {
                        body.SetFloat("_Metallic", 0.02f);
                    }

                    if (body.HasProperty("_Glossiness"))
                    {
                        body.SetFloat("_Glossiness", 0.9f);
                    }

                    if (joint.HasProperty("_Metallic"))
                    {
                        joint.SetFloat("_Metallic", 0.98f);
                    }

                    if (joint.HasProperty("_Glossiness"))
                    {
                        joint.SetFloat("_Glossiness", 0.86f);
                    }

                    innerBody = new Material(body)
                    {
                        name = "OrbitalRing.GlassInterior",
                        renderQueue = 2999,
                    };
                }

                return body;
            }
        }

        private static Material TankGlassMaterial()
        {
            var prefab = LDB.items.Select(2106)?.prefabDesc?.prefab;
            if (prefab == null)
            {
                return null;
            }

            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material != null && material.shader.name.Contains("PBR Standard Tank"))
                    {
                        return material;
                    }
                }
            }

            return null;
        }

        private static void Transparent(Material material, int queue)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_Mode"))
            {
                material.SetFloat("_Mode", 3);
                material.SetInt("_SrcBlend", (int)BlendMode.One);
                material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHABLEND_ON");
                material.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            }

            material.renderQueue = queue;
        }

        internal static void Dispose()
        {
            UnityEngine.Object.Destroy(body);
            UnityEngine.Object.Destroy(joint);
            UnityEngine.Object.Destroy(detail);
            UnityEngine.Object.Destroy(innerBody);
            body = null;
            joint = null;
            detail = null;
            innerBody = null;
        }
    }
}
