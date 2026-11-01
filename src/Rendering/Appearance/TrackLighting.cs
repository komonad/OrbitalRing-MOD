using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    internal static class TrackLighting
    {
        private static Cubemap steelEnvironment;
        private static readonly int reflectionCube0 = Shader.PropertyToID("unity_SpecCube0");
        private static readonly int reflectionCube1 = Shader.PropertyToID("unity_SpecCube1");
        private static readonly int reflectionCube0Hdr = Shader.PropertyToID("unity_SpecCube0_HDR");
        private static readonly int reflectionCube1Hdr = Shader.PropertyToID("unity_SpecCube1_HDR");
        private static readonly int reflectionBoxMin = Shader.PropertyToID("unity_SpecCube0_BoxMin");
        private static readonly int reflectionProbe0 = Shader.PropertyToID("unity_SpecCube0_ProbePosition");
        private static readonly int reflectionProbe1 = Shader.PropertyToID("unity_SpecCube1_ProbePosition");
        private static MaterialPropertyBlock steelLighting;

        internal static void Reset()
        {
            if (steelEnvironment != null)
            {
                UnityEngine.Object.Destroy(steelEnvironment);
            }

            steelEnvironment = null;
            steelLighting = null;
        }

        internal static MaterialPropertyBlock SteelLighting(Vector3 radial)
        {
            if (steelLighting != null)
            {
                return steelLighting;
            }

            const int size = 64;
            var up = radial.sqrMagnitude > 1 ? radial.normalized : Vector3.up;
            steelEnvironment = new Cubemap(size, TextureFormat.RGBAHalf, true)
            {
                name = "OrbitalRing.SteelEnvironment",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            for (int face = 0; face < 6; face++)
            {
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float a = (x + 0.5f) / size * 2 - 1;
                        float b = (y + 0.5f) / size * 2 - 1;
                        Vector3 direction;
                        switch ((CubemapFace)face)
                        {
                            case CubemapFace.PositiveX:
                                direction = new Vector3(1, -b, -a);
                                break;
                            case CubemapFace.NegativeX:
                                direction = new Vector3(-1, -b, a);
                                break;
                            case CubemapFace.PositiveY:
                                direction = new Vector3(a, 1, b);
                                break;
                            case CubemapFace.NegativeY:
                                direction = new Vector3(a, -1, -b);
                                break;
                            case CubemapFace.PositiveZ:
                                direction = new Vector3(a, -b, 1);
                                break;
                            default:
                                direction = new Vector3(-a, -b, -1);
                                break;
                        }

                        float elevation = Vector3.Dot(direction.normalized, up);
                        float sky = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-0.12f, 0.35f, elevation));
                        float horizon = Mathf.Exp(-elevation * elevation / 0.018f);
                        Color skyReflection = Color.Lerp(
                            new Color(0.08f, 0.085f, 0.09f), new Color(0.32f, 0.34f, 0.36f), sky);
                        pixels[y * size + x] = skyReflection + new Color(0.83f, 0.82f, 0.80f) * horizon;
                        pixels[y * size + x].a = 1;
                    }
                }

                steelEnvironment.SetPixels(pixels, (CubemapFace)face);
            }

            steelEnvironment.Apply(true, true);
            steelLighting = new MaterialPropertyBlock();
            // 轨道绘制通过材质属性块获得近似的天空反射。
            WriteSteelReflection(steelLighting);
            return steelLighting;
        }

        internal static void Apply(MaterialPropertyBlock properties, Vector3 radial)
        {
            SteelLighting(radial);
            WriteSteelReflection(properties);
        }

        private static void WriteSteelReflection(MaterialPropertyBlock properties)
        {
            properties.SetTexture(reflectionCube0, steelEnvironment);
            properties.SetTexture(reflectionCube1, steelEnvironment);
            properties.SetVector(reflectionCube0Hdr, new Vector4(1, 1, 0, 0));
            properties.SetVector(reflectionCube1Hdr, new Vector4(1, 1, 0, 0));
            properties.SetVector(reflectionBoxMin, new Vector4(0, 0, 0, 1));
            properties.SetVector(reflectionProbe0, Vector4.zero);
            properties.SetVector(reflectionProbe1, Vector4.zero);
        }

        internal static void Dispose() => Reset();
    }

    internal static class TrackRendering
    {
        internal static void ApplyMetalLighting(MaterialPropertyBlock properties, Vector3 radial)
        {
            TrackLighting.Apply(properties, radial);
        }
    }
}
