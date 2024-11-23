using Comgenie.Storage.Entities;
using Comgenie.Storage.Locations;
using Comgenie.Utils;
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Comgenie.Storage
{
    public partial class StoragePool : IDisposable
    {
        private Task? SyncTask = null;
        private bool SyncTaskRunning = false;
        private List<StorageLocationInfo> LocationInfos { get; set; } = new List<StorageLocationInfo>();
        public StoragePool() { }

        /// <summary>
        /// Add a storage location to this storage pool. 
        /// Note that a syncInterval of at least 60 is required when the storage location is used by multiple clients.
        /// </summary>
        /// <param name="storageLocation"></param>
        /// <param name="encryptionKey">Key used to encrypt data stored at this storage location</param>
        /// <param name="syncInterval">Amount of seconds max till changes are synced to/from this location. Set to null to directly sync changes with supported storage locations</param>
        /// <param name="priority">A lower number means a prefered storage location which will be used for any initial reads/writes before sync. When two storage locations use the same priority, they will be chosen at random for each read/write.</param>
        /// <param name="shared">If set to true, this storage location can be used by multiple clients at the same time. Note that this can not be used for the primary storage location and will require a sync interval.</param>
        /// <param name="repairPercent">A percentage amount of repair data to be included, set to null to disable.</param>
        /// <param name="tagFilters">Optional, only store items with tags starting with one of these tag filters.</param>
        public async Task AddStorageLocationAsync(IStorageLocation storageLocation, byte[] encryptionKey, int? syncInterval=null, int priority=1, bool shared=false, bool enableRepairData=false, string[]? tagFilters=null)
        {
            if (shared && !syncInterval.HasValue)
                throw new ArgumentException("Shared cannot be set without sync interval.");

            var setting = new StorageLocationInfo()
            {
                Location = storageLocation,
                EncryptionKey = encryptionKey,
                SyncInterval = syncInterval,
                Priority = priority,
                Shared = shared,
                EnableRepairData = enableRepairData,
                TagFilters = tagFilters
            };

            await setting.LoadIndexAsync();

            lock (this)
            {
                LocationInfos.Add(setting);

                // Make sure the best location is at top
                LocationInfos = LocationInfos.OrderBy(a => a.Priority).ToList();
            }

            if (LocationInfos.Count > 1 && SyncTask == null)
            {
                SyncTask = Task.Run(async () =>
                {
                    SyncTaskRunning = true;
                    while (SyncTaskRunning)
                    {
                        await SyncChangesAsync();
                        await Task.Delay(500);
                    }
                });
            }

            await SyncFullAsync();
        }        
        
        public void RemoveStorageLocation(IStorageLocation storageLocation)
        {
            // TODO: Sync this location first

            lock (this)
            {
                LocationInfos.RemoveAll(a => a.Location == storageLocation);
            }
        }
        private string GetStorageItemFileName(StorageItem item)
        {
            if (item.StorageLocationInfo == null)
                throw new ArgumentException("Storage item is not attached to a storage location");

            // Turn our item id and the encryption key (so file names cannot be found by brute force) into a file name suitable for disk storage
            var bytes = Encoding.UTF8.GetBytes(item.Id).Concat(new byte[] { 0 }).Concat(item.StorageLocationInfo.EncryptionKey).ToArray();
            var hash = SHA256.Create().ComputeHash(bytes);
            return string.Join("", hash.Take(16).Select(a => a.ToString("x2")));
        }

        public async Task SyncChangesAsync(bool ignoreSyncInterval=false)
        {
            foreach (var locationInfo in LocationInfos)
            {
                // Skip if there are no changes or if the previous sync is too recent for this storage location
                if (!locationInfo.Available || locationInfo.ChangesQueue.Count == 0 || (!ignoreSyncInterval && locationInfo.SyncInterval.HasValue && locationInfo.LastSync.HasValue && locationInfo.LastSync.Value.AddSeconds(locationInfo.SyncInterval.Value) > DateTime.UtcNow))
                    continue;

                // Sync changes
                while (locationInfo.ChangesQueue.TryDequeue(out var change))
                {
                    await SyncItem(change.Item, locationInfo, change.DataChanged);                                    
                }
                locationInfo.LastSync = DateTime.UtcNow;
            }
        }
        private async Task SyncItem(StorageItem sourceItem, StorageLocationInfo targetLocationInfo, bool includeData=true)
        {
            if (sourceItem.StorageLocationInfo == null || sourceItem.StorageLocationInfo == targetLocationInfo)
                throw new ArgumentException("Cannot sync to the same location, or from an undefined location");

            if (targetLocationInfo.Index == null)
                return; // Index not loaded (yet)

            if (sourceItem.Length == -1) // Deleted
            {
                if (targetLocationInfo.Index.Items.TryGetValue(sourceItem.Id, out StorageItem? ourItem))
                {
                    targetLocationInfo.Location.DeleteFile(ourItem.Id);
                    ourItem.Length = -1;
                }
            }
            else // Updated
            {
                if (!targetLocationInfo.Index.Items.TryGetValue(sourceItem.Id, out StorageItem? ourItem))
                {
                    // New item
                    ourItem = new StorageItem()
                    {
                        Id = sourceItem.Id,
                        Created = sourceItem.Created,
                        LastModified = sourceItem.LastModified,
                        Length = sourceItem.Length,
                        StorageLocationInfo = targetLocationInfo
                    };
                    ourItem.UpdateTags(sourceItem.Tags.ToArray());
                    targetLocationInfo.Index.Items.TryAdd(ourItem.Id, ourItem);
                }
                else
                {
                    // Updated item
                    ourItem.UpdateTags(sourceItem.Tags.ToArray());
                    ourItem.Length = sourceItem.Length;
                    ourItem.LastModified = sourceItem.LastModified;
                    ourItem.Created = sourceItem.Created;
                }

                if (includeData)
                {
                    var sourceStream = sourceItem.StorageLocationInfo.Location.OpenFile(GetStorageItemFileName(sourceItem), FileMode.Open, FileAccess.Read);
                    if (sourceStream == null)
                        throw new Exception("Could not open source file for syncing");

                    var targetStream = targetLocationInfo.Location.OpenFile(GetStorageItemFileName(ourItem), FileMode.Create, FileAccess.Write);
                    if (targetStream == null)
                        throw new Exception("Could not open target file for syncing");

                    using (var source = new EncryptedAndRepairableStream(sourceStream, sourceItem.StorageLocationInfo.EncryptionKey, sourceItem.StorageLocationInfo.EnableRepairData))
                    using (var target = new EncryptedAndRepairableStream(targetStream, targetLocationInfo.EncryptionKey, targetLocationInfo.EnableRepairData))
                    {
                        if (source != null && target != null)
                            await source.CopyToAsync(target);
                    }
                }
                await targetLocationInfo.SaveIndexAsync();
            }
        }
        private async Task SyncFullAsync()
        {
            // Do an extensive sync by comparing the LastModified field of the items in the storage location indexes.
            foreach (var locationInfo in LocationInfos)
            {
                if (!locationInfo.Available)
                    continue;

                if (locationInfo.Shared)
                {
                    // Reload index file as it might be changed by another client
                    await locationInfo.LoadIndexAsync();
                }

                if (locationInfo.Index == null)
                    continue; // Index not loaded yet

                foreach (var item in locationInfo.Index.Items.Values)
                {
                    foreach (var locationInfoOther in LocationInfos)
                    {
                        if (!locationInfoOther.Available || locationInfoOther.Index == null)
                            continue;

                        if (locationInfoOther.Index.Items.TryGetValue(item.Id, out var otherItem))
                        {
                            if (otherItem.LastModified < item.LastModified)
                            {
                                // Sync changes to other location
                                await SyncItem(item, locationInfoOther);
                            }
                        }
                        else
                        {
                            // Create at other location
                            await SyncItem(item, locationInfoOther);
                        }
                    }
                }

                locationInfo.LastSync = DateTime.UtcNow;
            }
        }

        private StorageLocationInfo? GetStorageLocationForItem(string itemId, out StorageItem? item)
        {
            if (itemId != null)
            {
                foreach (var location in LocationInfos)
                {
                    if (location.Index != null && location.Index.Items.ContainsKey(itemId))
                    {
                        item = location.Index.Items[itemId];
                        return location;
                    }
                }
            }
            item = null;
            return null;
        }

        public async Task UpdateTagsAsync(string itemId, string[] newTags)
        {
            var storageLocation = GetStorageLocationForItem(itemId, out StorageItem? item);
            if (storageLocation == null || item == null)
                return;

            // Update tags
            var tagsChanged = item.UpdateTags(newTags);

            if (!tagsChanged)
                return;

            item.LastModified = DateTime.UtcNow;

            // Add to change queue of other storage locations
            foreach (var location in LocationInfos)
            {
                location.ChangesQueue.Enqueue(new StorageItemChange()
                {
                    Item = item,
                    LocationInfo = storageLocation,
                    DataChanged = false
                });
            }

            // Save index file
            await storageLocation.SaveIndexAsync();
        }
        private bool TagFilter(string[] tagFilters, string[]? tags)
        {
            if (tags == null || tags.Length == 0)
                return false;

            foreach (var filter in tagFilters)
            {
                foreach (var tag in tags)
                {
                    if (filter.StartsWith(tag))
                        return true;
                }
            }
            return false;
        }

        public async Task<bool> RenameAsync(string oldItemId, string newItemId)
        {
            var storageLocation = GetStorageLocationForItem(oldItemId, out StorageItem? oldItem);
            var storageLocationNew = GetStorageLocationForItem(newItemId, out StorageItem? newItem);
            if (storageLocation == null || oldItem == null || (storageLocationNew != null && newItem != null && newItem.Length >= 0))
                return false;

            if (storageLocation.Index == null)
                return false;

            newItem = new StorageItem()
            {
                Id = newItemId,
                Created = oldItem.Created,
                LastModified = DateTime.UtcNow, // We have to overwrite this as its used for syncing
                Length = oldItem.Length,
                Tags = oldItem.Tags.ToList()                
            };
            newItem.StorageLocationInfo = storageLocation; // this also adds the tags to the search tree
            storageLocation.Index.Items[newItemId] = newItem;

            // Set the old item as 'deleted'
            oldItem.Length = -1;
            oldItem.LastModified = DateTime.UtcNow;

            // Rename physical file
            var fileNameOld = GetStorageItemFileName(oldItem);
            var fileNameNew = GetStorageItemFileName(newItem);
            storageLocation.Location.MoveFile(fileNameOld, fileNameNew);

            // Set tags (which als updates it in the search tree)
            oldItem.UpdateTags(new string[] { });

            // Add to change queue (sync thread will sync this delete to other storage locations)
            foreach (var location in LocationInfos)
            {
                if (location == storageLocation)
                    continue;
                location.ChangesQueue.Enqueue(new StorageItemChange()
                {
                    Item = oldItem,
                    LocationInfo = storageLocation,
                    DataChanged = true
                });
                location.ChangesQueue.Enqueue(new StorageItemChange()
                {
                    Item = newItem,
                    LocationInfo = storageLocation,
                    DataChanged = true
                });
            }

            // Save index file
            await storageLocation.SaveIndexAsync();

            return true;
        }

        /// <summary>
        /// Open a file stored in one of the storage locations added to this storage pool. 
        /// The file will be opened as a stream which encrypts and adds repair data on the fly
        /// </summary>
        /// <param name="itemId">Identifier of the file to be opened</param>
        /// <param name="mode">File mode, If creation of files is not allowed according to the file mode, this method will return null</param>
        /// <param name="access">Read, Write or Both</param>
        /// <param name="overwriteTags">Optional, if set it will overwrite any existing tags</param>
        /// <returns>If the file can be opened using this mode, it will return a stream object allowing reads and/or writes depending on the access level</returns>
        /// <exception cref="Exception"></exception>
        public EncryptedAndRepairableStream? Open(string itemId, FileMode mode = FileMode.Open, FileAccess access = FileAccess.ReadWrite, string[]? overwriteTags=null)
        {
            StorageLocationInfo? storageLocation = null;
            StorageItem? item = null;
            foreach (var location in LocationInfos)
            {
                if (location.Index != null && location.Index.Items.ContainsKey(itemId))
                {
                    storageLocation = location;
                    item = location.Index.Items[itemId];
                    break;                    
                }
            }
            if (item == null && (mode == FileMode.Open || mode == FileMode.Truncate))
                return null;

            bool storageItemChanged = false;
            if (item == null || storageLocation == null)
            {
                // Find best location matching our tag filter
                storageLocation = LocationInfos.Where(a => a.Available && a.Index != null && (a.TagFilters == null || TagFilter(a.TagFilters, overwriteTags))).OrderBy(a => a.Priority).FirstOrDefault();
                if (storageLocation == null)
                    throw new Exception("No storage location available or none with matching tagfilters");

                item = new StorageItem()
                {
                    Id = itemId,
                    Created = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow,
                    Length = 0
                };                                       
                item.StorageLocationInfo = storageLocation;
                storageItemChanged = true;
                storageLocation.Index!.Items[itemId] = item;
            }

            if (overwriteTags != null)
                storageItemChanged = item.UpdateTags(overwriteTags);

            var fn = GetStorageItemFileName(item);
            if (File.Exists(fn) && (mode == FileMode.OpenOrCreate || mode == FileMode.Create || mode == FileMode.Truncate))
                File.Delete(fn);

            var fileStream = storageLocation.Location.OpenFile(fn, mode, access);
            if (fileStream == null)
                return null;

            var stream = new EncryptedAndRepairableStream(fileStream, storageLocation.EncryptionKey, storageLocation.EnableRepairData);

            stream.OnDispose = async (streamWasWrittenTo) =>
            {                
                if (!streamWasWrittenTo && !storageItemChanged)
                    return;

                // File updated, Add to change queue (sync thread will sync this delete to other storage locations)
                // TODO: Only sync specific changed blocks
                //          (Can also be used for repairing corrupted parts of a file where there is a good backup)
                item.LastModified = DateTime.UtcNow;
                item.Length = stream.Length;

                foreach (var location in LocationInfos)
                {
                    if (location == storageLocation)
                        continue;
                    location.ChangesQueue.Enqueue(new StorageItemChange()
                    {
                        Item = item,
                        LocationInfo = storageLocation,
                        DataChanged = streamWasWrittenTo
                    });
                }

                // Save index file                
                await storageLocation.SaveIndexAsync();
            };
            return stream;
        }

        /// <summary>
        /// Delete a file from all connected locations in this storage pool.
        /// </summary>
        /// <param name="itemId">Identifier of the file to delete</param>
        public async Task DeleteAsync(string itemId)
        {
            var storageLocation = GetStorageLocationForItem(itemId, out StorageItem? item);
            if (storageLocation == null || item == null)
                return;

            // Update index
            item.Length = -1;
            item.LastModified = DateTime.UtcNow;

            // Delete physical file
            storageLocation.Location.DeleteFile(GetStorageItemFileName(item));

            // Remove tags (which als removes it from the search tree)
            item.UpdateTags(new string[] { });

            // Add to change queue (sync thread will sync this delete to other storage locations)
            foreach (var location in LocationInfos)
            {
                if (location == storageLocation)
                    continue;
                location.ChangesQueue.Enqueue(new StorageItemChange()
                {
                    Item = item,
                    LocationInfo = storageLocation,
                    DataChanged = true
                });
            }

            // Save index file
            await storageLocation.SaveIndexAsync();
        }


        public StorageItem? GetStorageItemById(string itemId)
        {
            StorageItem? item = null;
            GetStorageLocationForItem(itemId, out item);
            if (item == null || item.Length < 0)
                return null; // deleted item
            return item;
        }

        /// <summary>
        /// Return all unique items matching this filter. If multiple storage locations have the same file, only the item from the highest priority storage location is returned.
        /// </summary>
        /// <param name="filter">Filter, match tags exact or match the start of the tags by putting * at the end of the filter</param>
        /// <returns>List of storage items matching this filter</returns>
        public IEnumerable<StorageItem> List(string filter)
        {
            HashSet<string> uniqueIds = new HashSet<string>();
            foreach (var location in LocationInfos)
            {
                foreach (var item in location.Tree.SearchTreeItem(filter))
                {
                    if (item.Length >= 0 && uniqueIds.Add(item.Id))
                        yield return item;
                }
            }
        }

        public IQueryable<StorageItem> AsQueryable()
        {
            return new QueryTranslator<StorageItem>((string filter) =>
            {
                return List(filter);
            });
        }

        public IQueryable<T> AsQueryable<T>() where T : class
        {
            return new QueryTranslator<T>((string filter) =>
            {
                return ListAndConvert<T>(filter);                
            });
        }
        private IEnumerable<T> ListAndConvert<T>(string filter) where T : class
        {
            var storageItems = List(filter);
            foreach (var storageItem in storageItems)
            {
                // TODO: Create new T and fill properties (normal properties + tags)
                yield return default(T)!;
            }
        }

        public void Dispose()
        {
            // Note: Don't make this one async, as dispose is not automatically awaited

            // Save index files and release any locks
            SyncTaskRunning = false;
            if (SyncTask != null)
                SyncTask.Wait();

            // Execute one last manual sync to submit all changes to the other storage locations
            SyncChangesAsync(true).Wait();

            foreach (var locationInfo in LocationInfos)
                locationInfo.SaveIndexAsync(true).Wait();
        }
    }
}