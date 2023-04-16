using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Storage.Locations
{
    public class ArchiveStorageLocation : IStorageLocation
    {
        private string? ArchivePath { get; set; }
        public ArchiveStorageLocation() { }
        public ArchiveStorageLocation(string connectionString) {
            SetConnection(connectionString);
        }

        public void DeleteFile(string path)
        {
            
        }

        public bool IsAvailable()
        {
            if (ArchivePath == null)
                return false;
            return true;
        }

        public Stream? OpenFile(string path, FileMode mode, FileAccess access)
        {
            throw new NotImplementedException();
        }

        public void SetConnection(string connectionString)
        {
            ArchivePath = connectionString;
            if (!File.Exists(connectionString))
            {
                var ms = new MemoryStream();
                var zip = new ZipArchive(ms);
                
            }
        }
    }
}
