using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Util
{
    /// <summary>
    /// Configuration settings globally used for all Comgenie libraries within this proces.
    /// </summary>
    public class GlobalConfiguration
    {
        /// <summary>
        /// Folder to keep all the key files (pfx key, pfx certificates, DKIM keys, KeyStore keys)
        /// It is recommended to limit access to this folder.
        /// </summary>
        public static string SecretsFolder { get; set; } = ".";

    }
}
