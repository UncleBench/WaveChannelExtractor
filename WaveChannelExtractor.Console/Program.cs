using System;
using System.IO;
using System.Threading;

namespace WaveChannelExtractor.Console
{
    internal class Program
    {
        static void Main(string[] args)
        {
            string inputFile = null;
            string outputDir = null;

            // Parse CLI args
            foreach (var arg in args)
            {
                if (inputFile == null) inputFile = arg;
                else if (outputDir == null) outputDir = arg;
            }

            // Prompt for input file until valid
            while (true)
            {
                if (string.IsNullOrWhiteSpace(inputFile))
                {
                    System.Console.WriteLine("Enter path to input WAV file:");
                    inputFile = System.Console.ReadLine()?.Trim('"', ' ', '\t');
                }

                if (!File.Exists(inputFile))
                {
                    System.Console.WriteLine("File does not exist.");
                    inputFile = null;
                    continue;
                }

                if (!string.Equals(Path.GetExtension(inputFile), ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    System.Console.WriteLine("Input file must be a .wav file.");
                    inputFile = null;
                    continue;
                }

                // Check multiple channels using NAudio
                try
                {
                    using (var reader = new NAudio.Wave.WaveFileReader(inputFile))
                    {
                        if (reader.WaveFormat.Channels < 2)
                        {
                            System.Console.WriteLine("Input WAV file must have multiple channels.");
                            inputFile = null;
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"Error reading WAV file: {ex.Message}");
                    inputFile = null;
                    continue;
                }

                break; // Passed all checks, exit loop
            }

            // Derive default output folder if none provided
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                string dir = Path.GetDirectoryName(inputFile);
                string baseName = Path.GetFileNameWithoutExtension(inputFile);
                outputDir = Path.Combine(dir, baseName + "_extracted");
            }

            // Handle existing output directory
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
                System.Console.WriteLine($"Deleted existing output directory: {outputDir}");
            }

            // Load channel config
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "channel-config.txt");
            var channelAssignments = ChannelConfigLoader.Load(configPath);
            if (channelAssignments.Length == 0)
            {
                System.Console.WriteLine("Channel configuration is empty.");
                return;
            }

            var cts = new CancellationTokenSource();

            System.Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                System.Console.WriteLine("\nCancellation requested...");
                cts.Cancel();
            };

            try
            {
                ChannelExtractor.Extract(
                    inputFile,
                    outputDir,
                    channelAssignments,
                    cts.Token,
                    ReportProgress);

                System.Console.WriteLine("\n\nExtraction complete.");

                System.Console.WriteLine("\nExtracted channel files:");
                foreach (var ch in ChannelExtractor.GetOutputFilePaths(outputDir, channelAssignments))
                {
                    System.Console.WriteLine($"- {ch}");
                }
            }
            catch (OperationCanceledException)
            {
                System.Console.WriteLine("\nExtraction cancelled.");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"\nError: {ex.Message}");
            }

            System.Console.WriteLine("Press any key to exit.");
            System.Console.ReadKey();
        }

        static void ReportProgress(ExtractionProgress progress)
        {
            System.Console.Write($"\rProgress: {progress.Percent,3}% | Frames: {progress.FramesProcessed}/{progress.TotalFrames} | " +
                  $"Elapsed: {progress.Elapsed:mm\\:ss} | ETA: {(progress.EstimatedRemaining.HasValue ? progress.EstimatedRemaining.Value.ToString("mm\\:ss") : "--:--")}   ");
        }
    }
}
