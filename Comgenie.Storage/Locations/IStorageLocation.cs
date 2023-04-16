using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Storage.Locations
{
    public interface IStorageLocation
    {
        void SetConnection(string connectionString);
        Stream? OpenFile(string path, FileMode mode, FileAccess access);
        void DeleteFile(string path);
        bool IsAvailable();
    }
}
