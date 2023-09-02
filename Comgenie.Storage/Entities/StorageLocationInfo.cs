using Comgenie.Storage.Locations;
using Comgenie.Storage.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Comgenie.Storage.Entities
{
    internal class StorageLocationInfo
    {
        public IStorageLocation Location { get; set; }
        public int? SyncInterval { get; set; }
        public int Priority { get; set; }
        public double? RepairPercent { get; set; }
        public byte[] EncryptionKey { get; set; }
        public string[]? TagFilters { get; set; } = null;
        public bool Shared { get; set; } = false;
        public StoragePoolIndex Index { get; set; }
        public SuperTree<StorageItem> Tree { get; set; } = new SuperTree<StorageItem>();
        public DateTime? LastSync { get; set; }
        public ConcurrentQueue<StorageItemChange> ChangesQueue { get; set; } = new ConcurrentQueue<StorageItemChange>();
        private bool LastAvailableStatus { get; set; }
        private DateTime LastAvailableStatusCheck { get; set; } = DateTime.MinValue;
        public bool Available
        {
            get
            {
                if (LastAvailableStatusCheck.AddMinutes(5) < DateTime.UtcNow)
                {
                    LastAvailableStatus = Location.IsAvailable();
                    LastAvailableStatusCheck = DateTime.UtcNow;
                }
                return LastAvailableStatus;
            }
        }

        /// <summary>
        /// Load or create index file from this storage location. If an Index file was already loaded, it will be merged with the new index file.
        /// </summary>
        /// <param name="loadOnly">When set to true, a new index file won't be created if none was found</param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task LoadIndexAsync(bool loadOnly=false)
        {
            // Load index file
            var file = Location.OpenFile("index.cmg", FileMode.Open, FileAccess.Read);

            if (file == null)
            {
                if (loadOnly)
                    return;
                // New location, write new index file
                Index = new StoragePoolIndex();
                await SaveIndexAsync();
            }
            else
            {
                // Deserialize 
                using (var encStream = new EncryptedAndRepairableStream(file, EncryptionKey, RepairPercent))
                {
                    var newIndex = await JsonSerializer.DeserializeAsync<StoragePoolIndex>(encStream);
                    if (newIndex == null)
                        throw new Exception("Index file could not be deserialized. Corrupt?");

                    if (Index == null || Index.LastModified != newIndex.LastModified)
                    {
                        // Index file is changed by another client
                        if (Index != null)
                        {
                            // Merge storage items, try to keep as much of our own items as possible to prevent unnecessary adds/removes to the search tree
                            foreach (var item in Index.Items.Values)
                            {
                                if (newIndex.Items.TryGetValue(item.Id, out var newIndexItem))
                                {
                                    if (newIndexItem.LastModified > item.LastModified)
                                    {
                                        // This storage item is updated by another client, we will forget our existing item
                                        item.StorageLocationInfo = null;
                                    }
                                    else
                                    {
                                        // This storage item is the same or we modified this storage item, keep our item
                                        newIndex.Items.TryUpdate(item.Id, item, newIndexItem);
                                    }
                                }
                                else
                                {
                                    // We got a new item which isn't added to the saved Index file yet, add this storage item to the new index
                                    newIndex.Items.TryAdd(item.Id, item);
                                }                                                                
                            }                            
                        }

                        foreach (var item in newIndex.Items.Values)
                            item.StorageLocationInfo = this;

                        Index = newIndex;
                    }                    
                }
                file.Dispose();
            }
        }
        /// <summary>
        /// Storage index file, this call might be buffered to prevent too many disk operations
        /// </summary>
        /// <param name="forced">Directly save Index file without waiting</param>
        /// <returns></returns>
        public async Task SaveIndexAsync(bool forced=false)
        {
            if (Shared)
            {
                var lockId = Guid.NewGuid().ToString();
                // Shared storage location, we will work with a lock file

                // Check first if there is already a lock
                while (true)
                {
                    var lockFile = Location.OpenFile("index-lock.cmg", FileMode.Open, FileAccess.Read);
                    if (lockFile != null)
                    {
                        // There is already a lock file
                        var line = await new StreamReader(lockFile).ReadLineAsync();
                        var moment = DateTime.Parse(line.Split('|')[0], CultureInfo.InvariantCulture);
                        if (moment.AddHours(1) > DateTime.UtcNow)
                        {
                            // Recent lock, we will wait
                            lockFile.Dispose();
                            await Task.Delay(3000);
                            continue;
                        }
                    }
                    else
                    {
                        // No lock file found! We will create one
                        lockFile = Location.OpenFile("index-lock.cmg", FileMode.Create, FileAccess.Write);
                        using (var writer = new StreamWriter(lockFile))
                            await writer.WriteLineAsync(DateTime.UtcNow.ToString(CultureInfo.InvariantCulture) + "|" + lockId);
                        lockFile.Dispose();

                        // Wait a while before the race condition check
                        await Task.Delay(3000);

                        // Race condition check, see if the lock id is still ours
                        lockFile = Location.OpenFile("index-lock.cmg", FileMode.Open, FileAccess.Read);
                        if (lockFile == null)
                            continue; // Some other client might have created and already deleted their lock file
                        
                        var line = await new StreamReader(lockFile).ReadLineAsync();
                        lockFile.Dispose();
                        if (line.Split('|')[1] != lockId)
                            continue; // Not our lock id, some other client got lucky

                        // We got a lock! 
                        break;
                    }                    
                }

                // Retrieve current index file and merge it with our index file
                await LoadIndexAsync(true);
            }

            lock (Index)
            {
                Index.LastModified = DateTime.UtcNow;
                using (var file = Location.OpenFile("index.cmg", FileMode.Create, FileAccess.Write))
                using (var encStream = new EncryptedAndRepairableStream(file, EncryptionKey, RepairPercent))
                    JsonSerializer.SerializeAsync(encStream, Index).Wait();
            }


            if (Shared)
            {
                // Delete lock file
                Location.DeleteFile("index-lock.cmg");
            }
        }
    }
}
