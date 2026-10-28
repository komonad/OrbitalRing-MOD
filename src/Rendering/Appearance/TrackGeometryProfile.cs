namespace ProjectOrbitalRing.Rendering
{
    /// <summary>轨道截面及孤立组件的尺寸，单位为 Unity 世界单位。</summary>
    internal sealed class TrackGeometryProfile
    {
        internal static TrackGeometryProfile Default { get; } = new TrackGeometryProfile();

        internal float Radius { get; }
        internal float CenterHeight { get; }
        internal float IsolatedHalfLength { get; }

        internal TrackGeometryProfile(
            float radius = 0.32f, float centerHeight = 0.34f, float isolatedHalfLength = 0.35f)
        {
            Radius = radius;
            CenterHeight = centerHeight;
            IsolatedHalfLength = isolatedHalfLength;
        }
    }
}
