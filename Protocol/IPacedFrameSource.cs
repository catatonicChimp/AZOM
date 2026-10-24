namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Latest-state producer the write loop pulls from through the one-shot pacing gate,
    /// the same way it pulls <see cref="LedFrameScheduler"/>. The frame is built when the
    /// gate opens, so nothing queues and a frame is never older than one pacing slot —
    /// unlike the FIFO, which keeps every frame and falls behind once demand passes the
    /// 4 ms drain rate.
    /// </summary>
    public interface IPacedFrameSource
    {
        /// <summary>Cheap check the write loop makes every iteration.</summary>
        bool MayHaveWork { get; }

        /// <summary>Stuffed size of a typical frame, for the write-budget check.</summary>
        int NominalFrameBytes { get; }

        /// <summary>Next frame built from current state, or null when nothing is due.</summary>
        byte[]? TryTakeFrame();
    }
}
