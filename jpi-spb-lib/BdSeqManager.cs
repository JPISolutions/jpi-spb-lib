using System;
using System.IO;

namespace SparkplugB.Publisher
{
    internal class BdSeqManager
    {
        private readonly string _filePath;

        public BdSeqManager(string filePath = "sparkplug_bdseq.txt")
        {
            _filePath = filePath;
        }

        public ulong Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var text = File.ReadAllText(_filePath);
                    if (ulong.TryParse(text, out var value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
                // Ignore and start from 0
            }

            return 0;
        }

        public void Save(ulong value)
        {
            try
            {
                File.WriteAllText(_filePath, value.ToString());
            }
            catch
            {
                // Log if needed
            }
        }

        public ulong IncrementAndSave(ulong current)
        {
            var next = current + 1;
            Save(next);
            return next;
        }
    }
}
