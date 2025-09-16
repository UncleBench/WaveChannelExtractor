using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WaveChannelExtractor.Console
{
    public static class ChannelExtractor
    {
        public static void Extract(
            string inputFile,
            string outputDir,
            bool skipUnused,
            string[] channelAssignments,
            CancellationToken cancellationToken,
            Action<ExtractionProgress> progressCallback)
        {
            using (var reader = new WaveFileReader(inputFile))
            {
                WaveFormat sourceFormat = reader.WaveFormat;
                int channels = sourceFormat.Channels;
                int bytesPerSample = sourceFormat.BitsPerSample / 8;
                int blockAlign = sourceFormat.BlockAlign;
                long totalFrames = reader.Length / blockAlign;

                Directory.CreateDirectory(outputDir);

                var selectedChannels = Enumerable.Range(0, channels)
                    .Where(i => i < channelAssignments.Length)
                    .Where(i => !skipUnused || !channelAssignments[i].ToLowerInvariant().Contains("(unused)"))
                    .Select(i => new
                    {
                        Index = i,
                        Name = channelAssignments[i]
                            .ToLowerInvariant()
                            .Replace(" ", "-")
                            .Replace("(", "")
                            .Replace(")", "")
                    })
                    .ToArray();

                var singleChannelFormat = new WaveFormat(sourceFormat.SampleRate, sourceFormat.BitsPerSample, 1);

                var writers = selectedChannels.ToDictionary(
                    c => c.Index,
                    c =>
                    {
                        string filePath = Path.Combine(outputDir, c.Name + ".wav");
                        return new WaveFileWriter(filePath, singleChannelFormat);
                    });

                int framesPerBuffer = 16384;
                byte[] buffer = new byte[blockAlign * framesPerBuffer];
                var channelBuffers = selectedChannels.ToDictionary(
                    c => c.Index,
                    c => new byte[bytesPerSample * framesPerBuffer]);

                long framesReadTotal = 0;
                int lastPercent = -1;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = reader.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    int framesRead = bytesRead / blockAlign;

                    Parallel.ForEach(selectedChannels, c =>
                    {
                        byte[] chBuf = channelBuffers[c.Index];

                        for (int frame = 0; frame < framesRead; frame++)
                        {
                            int srcOffset = frame * blockAlign + c.Index * bytesPerSample;
                            int destOffset = frame * bytesPerSample;

                            Buffer.BlockCopy(buffer, srcOffset, chBuf, destOffset, bytesPerSample);
                        }

                        writers[c.Index].Write(chBuf, 0, bytesPerSample * framesRead);
                    });

                    framesReadTotal += framesRead;

                    int percent = (int)((framesReadTotal * 100) / totalFrames);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        TimeSpan elapsed = stopwatch.Elapsed;
                        TimeSpan? eta = null;
                        if (framesReadTotal > 0)
                        {
                            double secPerFrame = elapsed.TotalSeconds / framesReadTotal;
                            eta = TimeSpan.FromSeconds(secPerFrame * (totalFrames - framesReadTotal));
                        }

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

                foreach (var writer in writers.Values)
                {
                    writer.Dispose();
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }

        public static IEnumerable<string> GetOutputFilePaths(string outputDir, string[] channelAssignments, bool skipUnused)
        {
            return channelAssignments
                .Select((name, index) => new { index, name })
                .Where(c => !skipUnused || !c.name.ToLowerInvariant().Contains("(unused)"))
                .Select(c =>
                {
                    string fileName = c.name
                        .ToLowerInvariant()
                        .Replace(" ", "-")
                        .Replace("(", "")
                        .Replace(")", "")
                        + ".wav";
                    return Path.Combine(outputDir, fileName);
                });
        }
    }
}
