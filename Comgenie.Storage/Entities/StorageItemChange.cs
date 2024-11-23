using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Storage.Entities
{
    internal class StorageItemChange
    {
        public required StorageLocationInfo LocationInfo { get; set; }
        public required StorageItem Item { get; set; }
        public required bool DataChanged { get; set; }
    }
}
