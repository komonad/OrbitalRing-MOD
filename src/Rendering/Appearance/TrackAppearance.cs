using System;
using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    /// <summary>轨道模型的外壁、材质与批次效果设置。</summary>
    internal sealed class TrackAppearance
    {
        /// <summary>管壁渲染类别。</summary>
        public TrackWallMode WallMode { get; }
        /// <summary>用于在相邻网格截面间展开纵向纹理坐标的周期。零表示坐标不具周期性。</summary>
        public float TextureCoordinatePeriod { get; }
        /// <summary>可见玻璃外壁半径与几何包络半径之比。</summary>
        public float WallRadiusFraction { get; }
        /// <summary>可见表面是否接收实时阴影。默认启用。</summary>
        public bool ReceiveShadows { get; }

        internal Action<TrackMaterialSet> ConfigureMaterials { get; }
        internal Action<TrackEffectContext, MaterialPropertyBlock> ApplyEffects { get; }
        internal Func<PlanetFactory, Vector3, float> TextureCoordinate { get; }

        /// <summary>定义缓存材质和可选的每批次效果。</summary>
        /// <param name="wallMode">管壁渲染类别。</param>
        /// <param name="configureMaterials">主线程回调，每轮资源生成时配置渲染器自有材质副本。</param>
        /// <param name="applyEffects">主线程绘制回调，将批次统一变量写入借用的属性块；该属性块在本次回调期间有效。</param>
        /// <param name="textureCoordinate">以工厂和世界空间中心线位置计算有限纵向纹理坐标的纯函数；null 使用局部弧长。工厂可用时调用。</param>
        /// <param name="textureCoordinatePeriod">纹理坐标回调的有限非负周期。正值将各相邻截面展开到最近的周期；零保留回调值。局部弧长坐标保持其默认映射。</param>
        /// <param name="wallRadiusFraction">可见玻璃外壁与几何包络的半径比，范围为 0.1 到 1。</param>
        /// <param name="receiveShadows">可见表面是否接收场景实时阴影；默认启用。</param>
        /// <exception cref="ArgumentOutOfRangeException">外壁模式、半径比例或坐标周期超出其支持范围。</exception>
        public TrackAppearance(
            TrackWallMode wallMode = TrackWallMode.Glass, Action<TrackMaterialSet> configureMaterials = null,
            Action<TrackEffectContext, MaterialPropertyBlock> applyEffects = null,
            Func<PlanetFactory, Vector3, float> textureCoordinate = null, float textureCoordinatePeriod = 0,
            float wallRadiusFraction = 1, bool receiveShadows = true)
        {
            if (!Enum.IsDefined(typeof(TrackWallMode), wallMode))
            {
                throw new ArgumentOutOfRangeException(nameof(wallMode));
            }

            if (float.IsNaN(textureCoordinatePeriod) || float.IsInfinity(textureCoordinatePeriod)
                || textureCoordinatePeriod < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(textureCoordinatePeriod));
            }

            if (float.IsNaN(wallRadiusFraction) || float.IsInfinity(wallRadiusFraction)
                || wallRadiusFraction < 0.1f || wallRadiusFraction > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(wallRadiusFraction));
            }

            ReceiveShadows = receiveShadows;
            WallRadiusFraction = wallRadiusFraction;
            WallMode = wallMode;
            ConfigureMaterials = configureMaterials;
            ApplyEffects = applyEffects;
            TextureCoordinate = textureCoordinate;
            TextureCoordinatePeriod = textureCoordinatePeriod;
        }
    }
}
