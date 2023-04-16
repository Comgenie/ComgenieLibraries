using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Schema;

namespace Comgenie.Storage.Locations
{
    public class DiskStorageLocation : IStorageLocation
    {
        private string? Path { get; set; }

        public DiskStorageLocation() { }
        public DiskStorageLocation(string connectionString)
        {
            SetConnection(connectionString);
        }
        
        public void DeleteFile(string path)
        {
            if (Path == null)
                throw new Exception("Disk storage location is missing required settings");
            File.Delete(System.IO.Path.Combine(Path, path));
        }


        public bool IsAvailable()
        {
            if (Path == null)
                return false;
            if (!Directory.Exists(Path))
                return false;
            return true;
        }


        public Stream? OpenFile(string path, FileMode mode, FileAccess access)
        {
            if (Path == null)
                throw new Exception("Disk storage location is missing required settings");

            var filePath = System.IO.Path.Combine(Path, path);

            if (!File.Exists(filePath) && mode == FileMode.Open || mode == FileMode.Truncate)
                return null;

            if (mode == FileMode.CreateNew && File.Exists(path))
                return null;

            return File.Open(filePath, mode, access); 
        }

        public void SetConnection(string connectionString)
        {
            Path = connectionString;
        }
    }
}
