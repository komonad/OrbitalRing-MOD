using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ProjectOrbitalRing.Rendering
{
    /// <summary>在单个类型的资源初始化期间配置的轨道渲染器自有材质。</summary>
    /// <remarks>纹理和着色器引用由外观模块持有。在初始化回调期间配置这些材质；绘制时状态应放入效果属性块。</remarks>
    internal sealed class TrackMaterialSet
    {
        private readonly Dictionary<TrackSurfaceRole, Material> materials =
            new Dictionary<TrackSurfaceRole, Material>();
        private readonly bool owned;
        private readonly TrackWallMode mode;
        private bool configuring;
        internal TrackWallMode WallMode => mode;
        /// <summary>外管壁材质。</summary>
        public Material Wall => materials[TrackSurfaceRole.Wall];
        /// <summary>轨道金属构件所用的材质。</summary>
        public Material Fittings => materials[TrackSurfaceRole.Fittings];
        /// <summary>导体与粒子束材质。</summary>
        public Material Core => materials[TrackSurfaceRole.Core];
        /// <summary>结构附件和装饰模块材质。</summary>
        public Material Detail => materials[TrackSurfaceRole.Detail];

        internal TrackMaterialSet(TrackAppearance appearance)
        {
            owned = appearance != null;
            mode = appearance?.WallMode ?? TrackWallMode.Glass;
            materials[TrackSurfaceRole.Wall] = Copy(TrackMaterialDefaults.BodyMaterial);
            materials[TrackSurfaceRole.Fittings] = Copy(TrackMaterialDefaults.JointMaterial);
            materials[TrackSurfaceRole.Detail] = Copy(TrackMaterialDefaults.DetailMaterial);
            if (owned)
            {
                materials[TrackSurfaceRole.Core] = new Material(Shader.Find("Standard"));
                configuring = true;
                try
                {
                    appearance.ConfigureMaterials?.Invoke(this);
                    ConfigureWall();
                    materials[TrackSurfaceRole.InnerWall] = new Material(Wall)
                    {
                        renderQueue = 2999,
                    };
                }
                catch
                {
                    Dispose();
                    throw;
                }
                finally
                {
                    configuring = false;
                }
            }
            else
            {
                materials[TrackSurfaceRole.InnerWall] = TrackMaterialDefaults.InnerWallMaterial;
                materials[TrackSurfaceRole.Core] = null;
            }
        }

        private Material Copy(Material material) => owned ? new Material(material) : material;
        /// <summary>将提供的材质复制到可配置的表面角色。</summary>
        /// <param name="role">目标角色；内壁由系统自动派生。</param>
        /// <param name="source">其纹理和着色器在整个渲染期间保持有效的材质。</param>
        /// <exception cref="InvalidOperationException">在材质配置回调之外调用此方法。</exception>
        /// <exception cref="ArgumentNullException">源材质为 null。</exception>
        /// <exception cref="ArgumentOutOfRangeException">角色无效，或指向派生的内壁。</exception>
        public void SetMaterial(TrackSurfaceRole role, Material source)
        {
            if (!configuring)
            {
                throw new InvalidOperationException("Material replacement belongs in the configuration callback.");
            }

            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (role == TrackSurfaceRole.InnerWall || !materials.ContainsKey(role))
            {
                throw new ArgumentOutOfRangeException(nameof(role));
            }

            var replacement = new Material(source);
            UnityEngine.Object.Destroy(materials[role]);
            materials[role] = replacement;
        }

        private void ConfigureWall()
        {
            if (Wall.shader == null || !Wall.shader.isSupported)
            {
                throw new InvalidOperationException("The orbital wall shader is unavailable.");
            }

            bool glass = mode == TrackWallMode.Glass;
            Wall.SetOverrideTag("RenderType", glass ? "Transparent" : "Opaque");
            Wall.renderQueue = glass ? 3000 : 2000;
            if (Wall.HasProperty("_Mode"))
            {
                Wall.SetFloat("_Mode", glass ? 3 : 0);
                Wall.SetInt("_SrcBlend", (int)BlendMode.One);
                Wall.SetInt("_DstBlend", (int)(glass ? BlendMode.OneMinusSrcAlpha : BlendMode.Zero));
                Wall.SetInt("_ZWrite", glass ? 0 : 1);
                Wall.DisableKeyword("_ALPHATEST_ON");
                Wall.DisableKeyword("_ALPHABLEND_ON");
                if (glass)
                {
                    Wall.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                }
                else
                {
                    Wall.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                }
            }
        }

        internal Material Get(TrackSurfaceRole role) => materials[role];

        internal void Dispose()
        {
            if (owned)
            {
                foreach (var material in materials.Values)
                {
                    if (material != null)
                    {
                        UnityEngine.Object.Destroy(material);
                    }
                }
            }

            materials.Clear();
        }
    }
}
