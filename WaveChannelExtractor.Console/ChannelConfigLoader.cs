using System;
using System.IO;
using System.Linq;

namespace WaveChannelExtractor.Console
{
    public static class ChannelConfigLoader
    {
        public static string[] Load(string configFilePath)
        {
            if (!File.Exists(configFilePath))
            {
                System.Console.WriteLine($"Missing config file: {configFilePath}");
                return Array.Empty<string>();
            }

            return File.ReadAllLines(configFilePath)
                       .Where(line => !string.IsNullOrWhiteSpace(line))
                       .ToArray();
        }
    }
}
