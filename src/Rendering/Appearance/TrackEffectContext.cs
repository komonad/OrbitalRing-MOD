using UnityEngine;

namespace ProjectOrbitalRing.Rendering
{
    /// <summary>单个空间和语义批次的渲染回调上下文。</summary>
    internal readonly struct TrackEffectContext
    {
        /// <summary>正在渲染的工厂。</summary>
        public PlanetFactory Factory { get; }
        /// <summary>本批次中具有代表性的已完成实体。</summary>
        public int EntityId { get; }
        /// <summary>由类型提供的稳定语义分组键。</summary>
        public int GroupKey { get; }
        /// <summary>Unity 动画时间，单位为秒。</summary>
        public float TimeSeconds { get; }
        /// <summary>正在绘制的表面。</summary>
        public TrackSurfaceRole Surface { get; }

        internal TrackEffectContext(PlanetFactory factory, int entityId, int groupKey, TrackSurfaceRole surface)
        {
            Factory = factory;
            EntityId = entityId;
            GroupKey = groupKey;
            TimeSeconds = Time.time;
            Surface = surface;
        }
    }
}
