using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace WaveChannelExtractor.Console
{
    public static class ChannelExtractor
    {
        public static void Extract(
            string inputFile,
            string outputDir,
            string[] channelAssignments,
            CancellationToken cancellationToken,
            Action<ExtractionProgress> progressCallback)
        {
            using (var reader = new WaveFileReader(inputFile))
            {
                var format = reader.WaveFormat;
                ValidateFormat(format);

                int bytesPerSample = GetBytesPerSample(format);
                int blockAlign = format.BlockAlign;
                long totalFrames = reader.Length / blockAlign;

                Directory.CreateDirectory(outputDir);

                List<ChannelInfo> channels = PrepareChannelMetadata(channelAssignments);
                var groups = DetectChannelGroups(channels);

                var writers = InitializeWriters(format, groups.StereoPairs, groups.MonoChannels, outputDir);
                int framesPerBuffer = 16384;
                byte[] inputBuffer = new byte[blockAlign * framesPerBuffer];

                ProcessAudio(
                    reader, writers,
                    groups.StereoPairs,
                    groups.MonoChannels,
                    inputBuffer,
                    blockAlign,
                    bytesPerSample,
                    totalFrames,
                    cancellationToken,
                    progressCallback
                );

                DisposeWriters(writers);
            }
        }

        private static void ValidateFormat(WaveFormat format)
        {
            if (format.Encoding != WaveFormatEncoding.Pcm && format.Encoding != WaveFormatEncoding.IeeeFloat)
                throw new InvalidOperationException("Only PCM or IEEE float formats are supported.");

            int bytesPerSample = GetBytesPerSample(format);
            if (format.BlockAlign != bytesPerSample * format.Channels)
                throw new InvalidDataException("Block alignment mismatch.");
        }

        private static int GetBytesPerSample(WaveFormat format)
        {
            return format.Encoding == WaveFormatEncoding.IeeeFloat ? 4 : format.BitsPerSample / 8;
        }

        private static List<ChannelInfo> PrepareChannelMetadata(string[] assignments)
        {
            var result = new List<ChannelInfo>();
            for (int i = 0; i < assignments.Length; i++)
            {
                string raw = assignments[i];
                if (raw.ToLowerInvariant().Contains("(unused)"))
                    continue;

                string cleaned = raw
                    .ToLowerInvariant()
                    .Replace("(", "")
                    .Replace(")", "")
                    .Replace(" ", "-");

                result.Add(new ChannelInfo { Index = i, Name = cleaned });
            }
            return result;
        }

        private static ChannelGroup DetectChannelGroups(List<ChannelInfo> channels)
        {
            var stereoPairs = new Dictionary<string, StereoGroup>(StringComparer.OrdinalIgnoreCase);
            var monoChannels = new List<ChannelInfo>();

            foreach (var ch in channels)
            {
                string name = ch.Name;
                if (string.IsNullOrEmpty(name))
                {
                    monoChannels.Add(ch);
                    continue;
                }

                string lowerName = name.ToLowerInvariant();
                char lastChar = lowerName[lowerName.Length - 1];

                if (lastChar == 'l' || lastChar == 'r')
                {
                    string baseName = GetBaseName(lowerName);

                    if (!stereoPairs.ContainsKey(baseName))
                        stereoPairs[baseName] = new StereoGroup();

                    if (lastChar == 'l')
                        stereoPairs[baseName].Left = ch.Index;
                    else
                        stereoPairs[baseName].Right = ch.Index;
                }
                else
                {
                    monoChannels.Add(ch);
                }
            }

            // Move incomplete stereo pairs to mono list with proper names
            var confirmedStereoPairs = new Dictionary<string, StereoGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in stereoPairs)
            {
                var g = pair.Value;
                if (g.Left.HasValue && g.Right.HasValue)
                {
                    confirmedStereoPairs[pair.Key] = new StereoGroup
                    {
                        Left = g.Left,
                        Right = g.Right
                    };
                }
                else
                {
                    if (g.Left.HasValue)
                        monoChannels.Add(new ChannelInfo { Index = g.Left.Value, Name = pair.Key + "-l" });
                    if (g.Right.HasValue)
                        monoChannels.Add(new ChannelInfo { Index = g.Right.Value, Name = pair.Key + "-r" });
                }
            }

            return new ChannelGroup
            {
                StereoPairs = confirmedStereoPairs,
                MonoChannels = monoChannels
            };
        }

        private static string GetBaseName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 2)
                return name;

            // Remove last character ('l' or 'r')
            string baseName = name.Substring(0, name.Length - 1);

            // Trim trailing separators just in case
            return baseName.TrimEnd('-', '_', '.', ' ');
        }

        private static Dictionary<string, WaveFileWriter> InitializeWriters(
            WaveFormat sourceFormat,
            Dictionary<string, StereoGroup> stereoPairs,
            List<ChannelInfo> monoChannels,
            string outputDir)
        {
            var writers = new Dictionary<string, WaveFileWriter>();

            foreach (var pair in stereoPairs)
            {
                string path = Path.Combine(outputDir, pair.Key + "-stereo.wav");
                var format = new WaveFormat(sourceFormat.SampleRate, sourceFormat.BitsPerSample, 2);
                writers[pair.Key] = new WaveFileWriter(path, format);
            }

            foreach (var mono in monoChannels)
            {
                string path = Path.Combine(outputDir, mono.Name + ".wav");
                var format = new WaveFormat(sourceFormat.SampleRate, sourceFormat.BitsPerSample, 1);
                writers[mono.Name] = new WaveFileWriter(path, format);
            }

            return writers;
        }

        private static void ProcessAudio(
            WaveFileReader reader,
            Dictionary<string, WaveFileWriter> writers,
            Dictionary<string, StereoGroup> stereoPairs,
            List<ChannelInfo> monoChannels,
            byte[] inputBuffer,
            int blockAlign,
            int bytesPerSample,
            long totalFrames,
            CancellationToken cancellationToken,
            Action<ExtractionProgress> progressCallback)
        {
            long framesReadTotal = 0;
            int lastPercent = -1;
            var stopwatch = Stopwatch.StartNew();

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = reader.Read(inputBuffer, 0, inputBuffer.Length);
                if (bytesRead == 0)
                    break;

                int framesRead = bytesRead / blockAlign;

                int validBytes = framesRead * blockAlign;

                var stereoBuffers = new Dictionary<string, byte[]>(stereoPairs.Count);
                var monoBuffers = new Dictionary<string, byte[]>(monoChannels.Count);

                foreach (var pair in stereoPairs)
                    stereoBuffers[pair.Key] = new byte[framesRead * 2 * bytesPerSample];

                foreach (var ch in monoChannels)
                    monoBuffers[ch.Name] = new byte[framesRead * bytesPerSample];

                System.Threading.Tasks.Parallel.ForEach(stereoPairs, pair =>
                {
                    var g = pair.Value;
                    var buffer = stereoBuffers[pair.Key];

                    for (int frame = 0; frame < framesRead; frame++)
                    {
                        int srcL = frame * blockAlign + g.Left.Value * bytesPerSample;
                        int srcR = frame * blockAlign + g.Right.Value * bytesPerSample;
                        int dst = frame * 2 * bytesPerSample;

                        Buffer.BlockCopy(inputBuffer, srcL, buffer, dst, bytesPerSample);
                        Buffer.BlockCopy(inputBuffer, srcR, buffer, dst + bytesPerSample, bytesPerSample);
                    }
                });

                System.Threading.Tasks.Parallel.ForEach(monoChannels, ch =>
                {
                    var buffer = monoBuffers[ch.Name];

                    for (int frame = 0; frame < framesRead; frame++)
                    {
                        int src = frame * blockAlign + ch.Index * bytesPerSample;
                        int dst = frame * bytesPerSample;

                        Buffer.BlockCopy(inputBuffer, src, buffer, dst, bytesPerSample);
                    }
                });

                foreach (var pair in stereoPairs)
                {
                    var buffer = stereoBuffers[pair.Key];
                    int bytesToWrite = framesRead * 2 * bytesPerSample;
                    writers[pair.Key].Write(buffer, 0, bytesToWrite);
                }

                foreach (var ch in monoChannels)
                {
                    var buffer = monoBuffers[ch.Name];
                    int bytesToWrite = framesRead * bytesPerSample;
                    writers[ch.Name].Write(buffer, 0, bytesToWrite);
                }

                framesReadTotal += framesRead;

                int percent = (int)((framesReadTotal * 100) / totalFrames);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    var elapsed = stopwatch.Elapsed;
                    TimeSpan? eta = framesReadTotal > 0
                        ? TimeSpan.FromSeconds(elapsed.TotalSeconds / framesReadTotal * (totalFrames - framesReadTotal))
                        : (TimeSpan?)null;

                    progressCallback?.Invoke(new ExtractionProgress
                    {
                        Percent = percent,
                        FramesProcessed = framesReadTotal,
                        TotalFrames = totalFrames,
                        Elapsed = elapsed,
                        EstimatedRemaining = eta
                    });
                }
            }

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
        }

        private static void DisposeWriters(Dictionary<string, WaveFileWriter> writers)
        {
            foreach (var writer in writers.Values)
            {
                writer.Dispose();
            }
        }

        public static IEnumerable<string> GetOutputFilePaths(string outputDir, string[] channelAssignments)
        {
            return channelAssignments
                .Select(x =>
                {
                    string fileName = x
                        .ToLowerInvariant()
                        .Replace(" ", "-")
                        .Replace("(", "")
                        .Replace(")", "")
                        + ".wav";
                    return Path.Combine(outputDir, fileName);
                });
        }

        // Helper classes
        private class ChannelInfo
        {
            public int Index { get; set; }
            public string Name { get; set; }
        }

        private class StereoGroup
        {
            public int? Left { get; set; }
            public int? Right { get; set; }
        }

        private class ChannelGroup
        {
            public Dictionary<string, StereoGroup> StereoPairs { get; set; }
            public List<ChannelInfo> MonoChannels { get; set; }
        }
    }
}
