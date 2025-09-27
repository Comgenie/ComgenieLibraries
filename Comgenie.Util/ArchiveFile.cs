using Comgenie.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Util
{
    public class ArchiveFile
    {
        private static ConcurrentDictionary<string, ArchiveFile> SharedInstances { get; set; } = new();
        public static ArchiveFile GetSharedInstance(string archiveName)
        {
            if (!SharedInstances.ContainsKey(archiveName))
                SharedInstances[archiveName] = new ArchiveFile(archiveName);
            return SharedInstances[archiveName];
        }

        private string IndexFileName { get; set; }
        private string DataFileName { get; set; }

        private ConcurrentDictionary<string, (long start, long size)> Entries { get; set; } = new();
        private long EndOfDataFile { get; set; } = 0;
        public ArchiveFile(string archiveName)
        {
            IndexFileName = archiveName + ".index";
            DataFileName = archiveName + ".data";

            var indexFileExists = File.Exists(IndexFileName);
            var dataFileExists = File.Exists(DataFileName);

            if (indexFileExists && !dataFileExists)
                throw new Exception("Data file missing");

            if (!indexFileExists && dataFileExists)
                throw new Exception("Index file missing");

            if (indexFileExists)
            {
                using (var file = File.OpenRead(archiveName + ".index"))
                using (var reader = new BinaryReader(file))
                {
                    try
                    {
                        while (true)
                        {
                            var entryName = reader.ReadString();
                            var entryStart = reader.ReadInt64();
                            var entrySize = reader.ReadInt64();
                            Entries[entryName] = (entryStart, entrySize);

                            if (EndOfDataFile < entryStart + entrySize)
                                EndOfDataFile = entryStart + entrySize;
                        }
                    }
                    catch (EndOfStreamException ex)
                    {
                        Console.WriteLine("We have " + Entries.Count + " entries.");
                    }
                }
            }
        }
        public bool Exists(string entryName)
        {
            return Entries.ContainsKey(entryName);
        }
        private List<SubStream> Streams = new();
        private SemaphoreSlim Semmie = new SemaphoreSlim(1);
        public async Task<Stream?> Open(string entryName)
        {
            if (!Entries.ContainsKey(entryName))
                return null;

            await Semmie.WaitAsync();

            // Clean up old streams
            var streams = Streams.ToArray();
            foreach (var stream in streams)
            {
                if (stream.IsDisposed)
                    Streams.Remove(stream);
            }

            var entry = Entries[entryName];
            var file = File.Open(DataFileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var subStream = new SubStream(file, entry.start, entry.size, true);
            Streams.Add(subStream);

            Semmie.Release();

            return subStream;
        }
        public async Task Add(string entryName, Stream data)
        {
            var buffer = new byte[1024 * 128];

            await Semmie.WaitAsync();

            // Wait till all streams are disposed
            while (true)
            {
                if (Streams.Any(a => !a.IsDisposed))
                {
                    await Task.Delay(1000);
                    continue;
                }
                break;
            }

            (long start, long size) newEntry = new();

            // Append data
            using (var file = await OpenFileRetry(DataFileName, FileMode.Append, FileAccess.Write))
            {
                newEntry.start = file.Position;
                while (true)
                {
                    var len = await data.ReadAsync(buffer, 0, buffer.Length);
                    if (len <= 0)
                        break;

                    await file.WriteAsync(buffer, 0, len);
                    newEntry.size += len;
                }
                EndOfDataFile = newEntry.start + newEntry.size;
            }

            // Append index file
            using (var file = await OpenFileRetry(IndexFileName, FileMode.Append, FileAccess.Write))
            using (var writer = new BinaryWriter(file))
            {
                
                writer.Write(entryName);
                writer.Write(newEntry.start);
                writer.Write(newEntry.size);
                //Console.WriteLine("After write entry");
            }

            //Console.WriteLine("Finished write entry");

            Entries[entryName] = newEntry;

            //Console.WriteLine("Release lock");
            Semmie.Release();
        }
        private static async Task<FileStream> OpenFileRetry(string path, FileMode mode, FileAccess access)
        {
            while (true)
            {
                try
                {
                    return File.Open(path, mode, access);
                }
                catch (IOException ex)
                {
                    Console.WriteLine(ex.ToString());
                    await Task.Delay(5000);
                }
            }
        }

        public async Task<string?> ReadAllTextAsync(string entryName)
        {
            using var file = await Open(entryName);
            if (file == null)
                return null;

            using var reader = new StreamReader(file);
            var str = await reader.ReadToEndAsync();
            return str;
        }
        public async Task WriteAllTextAsync(string entryName, string data)
        {
            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms);
            
            writer.Write(data);
            writer.Flush();
            
            ms.Position = 0;
            await Add(entryName, ms);
        }


        public static string GetHash(string data)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(data));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                    builder.Append(bytes[i].ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
