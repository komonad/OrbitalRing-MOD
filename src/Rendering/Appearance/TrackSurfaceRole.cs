namespace ProjectOrbitalRing.Rendering
{
    /// <summary>接收程序化渲染效果的表面。</summary>
    internal enum TrackSurfaceRole
    {
        /// <summary>外管壁。</summary>
        Wall,
        /// <summary>透明内壁。</summary>
        InnerWall,
        /// <summary>不透明接头、管箍和内部金属构件。</summary>
        Fittings,
        /// <summary>装饰性内部导体或梁。</summary>
        Core,
        /// <summary>结构附件、线圈和装饰模块的独立材质。</summary>
        Detail,
    }
}
