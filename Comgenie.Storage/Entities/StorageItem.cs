using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Storage.Entities
{
    public class StorageItem
    {
        public required string Id { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime Created { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public long Length { get; set; } = 0;        


        private StorageLocationInfo? _storageLocationInfo = null;
        internal StorageLocationInfo? StorageLocationInfo
        {
            get
            {
                return _storageLocationInfo;
            }
            set
            {
                if (value == _storageLocationInfo)
                    return;

                // Remove tags from old location info
                if (_storageLocationInfo != null)
                {
                    _storageLocationInfo.Tree.DeleteTreeItem("Id=" + Id, this);
                    foreach (var tag in Tags)
                        _storageLocationInfo.Tree.DeleteTreeItem(tag, this);
                }

                // Add tags to new location info
                if (value != null)
                {
                    value.Tree.AddTreeItem("Id=" + Id, this);
                    foreach (var tag in Tags)
                        value.Tree.AddTreeItem(tag, this);
                }

                _storageLocationInfo = value;
            }
        }
        internal bool UpdateTags(string[] newTags)
        {
            var changed = false;
            // Delete old tags
            foreach (var tag in Tags.ToList())
            {
                if (newTags.Contains(tag))
                    continue;
                Tags.Remove(tag);
                if (_storageLocationInfo != null)
                    _storageLocationInfo.Tree.DeleteTreeItem(tag, this);
                changed = true;
            }

            // Add any missing tags
            foreach (var tag in newTags)
            {
                if (Tags.Contains(tag))
                    continue;
                Tags.Add(tag);
                if (_storageLocationInfo != null)
                    _storageLocationInfo.Tree.AddTreeItem(tag, this);
                changed = true;
            }

            return changed;
        }
    }
}
