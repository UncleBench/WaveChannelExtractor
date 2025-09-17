using System.Collections.Generic;
using WaveChannelExtractor.Console;

namespace Reckie.Console
{
    internal class ChannelGroup
    {
        public Dictionary<string, StereoGroup> StereoPairs;
        public List<ChannelInfo> MonoChannels;
    }
}
