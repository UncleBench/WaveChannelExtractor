using System;

namespace WaveChannelExtractor.Console
{
    public class ExtractionProgress
    {
        public int Percent { get; set; }
        public long FramesProcessed { get; set; }
        public long TotalFrames { get; set; }
        public TimeSpan Elapsed { get; set; }
        public TimeSpan? EstimatedRemaining { get; set; }
    }
}
