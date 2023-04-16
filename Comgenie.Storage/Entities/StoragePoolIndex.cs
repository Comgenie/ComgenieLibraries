using Comgenie.Storage.Entities;
using System.Collections.Concurrent;

namespace Comgenie.Storage.Entities
{
    public class StoragePoolIndex
    {
        public ConcurrentDictionary<string, StorageItem> Items { get; set; } = new ConcurrentDictionary<string, StorageItem>();
        public DateTime LastModified { get; set; } = DateTime.MinValue;
    }
}