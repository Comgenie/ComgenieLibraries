using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Storage.Locations
{
    /// <summary>
    /// Generic interface to provide a simple Api for different types of storage locations. 
    /// </summary>
    public interface IStorageLocation
    {
        /// <summary>
        /// Used to provide the connection settings for this storage location. 
        /// </summary>
        /// <param name="connectionString">A string containing all information this storage location should have to make a connection.</param>
        void SetConnection(string connectionString);

        /// <summary>
        /// Open a file on this storage location. 
        /// </summary>
        /// <param name="path">Path within this storage location</param>
        /// <param name="mode">File mode, similar to the System.IO.File.Open implementation</param>
        /// <param name="access">Required file access level, similar to the System.IO.File.Open implementation</param>
        /// <returns>A stream object if the file could be opened succesfully for the requested file and access modes.</returns>
        Stream? OpenFile(string path, FileMode mode, FileAccess access);

        /// <summary>
        /// Try to delete the file with the provided path in this storage location.
        /// An exception will be thrown if this is not possible for any reason.
        /// </summary>
        /// <param name="path">Path within this storage location</param>
        void DeleteFile(string path);

        /// <summary>
        /// Try to move the file with the provided path in this storage location. This method will always overwrite any existing file existing on the newPath.
        /// An exception will be thrown if moving the file is not possible for any reason.
        /// </summary>
        /// <param name="oldPath">Existing path within this storage location</param>
        /// <param name="newPath">New path within this storage location. If another file exists on that location, it will be overwritten.</param>
        void MoveFile(string oldPath, string newPath);

        /// <summary>
        /// Check is this storage location is available and can be used.
        /// </summary>
        /// <returns>This storage location can be used to do all file actions.</returns>
        bool IsAvailable();
    }
}
