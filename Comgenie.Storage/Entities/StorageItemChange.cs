using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Storage.Entities
{
    internal class StorageItemChange
    {
        public StorageLocationInfo LocationInfo { get; set; }
        public StorageItem Item { get; set; }
        public bool DataChanged { get; set; }
    }
}
