using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.Utils
{
    /// <summary>
    /// Fast and hopefully memory efficient memory tree to quickly find items using filters.
    /// </summary>
    /// <typeparam name="T">Type of items to store</typeparam>
    public class SuperTree<T>
    {
        public int Precision = 16; // Min 1, A lower percision number means higher memory consumption, but possibly also higher speed. This means that for every 8 different characters, there will be 1 tree item
        public int Grouping = 1; // Min 1, This will group multiple characters together. Increasing this will increase speed, but also increases memory usage by a lot

        private int MaxTreeLevel = 0;
        private byte[] UnlikelyCharacters; // 256 bytes, marking if something is unlikely or not (0 is unlikely, anything else is the likely char index)
        private int LikelyCharacterCount = 0;
        private TreeNode Tree { get; set; }

        public SuperTree()
        {
            Tree = new TreeNode();

            // Build Unlikely characters lookup (focussing on readable text)
            UnlikelyCharacters = new byte[256];
            byte likelyCharacterIndex = 0;
            for (var i = 0; i < 256; i++)
            {
                if (i < 31 || i >= 128)
                    UnlikelyCharacters[i] = 255;
                else
                    UnlikelyCharacters[i] = likelyCharacterIndex++;
            }

            // Calculate max tree level
            LikelyCharacterCount = 0;
            for (var i = 0; i < 256; i++)
                if (UnlikelyCharacters[i] < 255)
                    LikelyCharacterCount++;

            MaxTreeLevel = (int)Math.Pow(LikelyCharacterCount, Grouping);
            MaxTreeLevel = (int)Math.Ceiling(MaxTreeLevel / (double)Precision);
            MaxTreeLevel++; // Other group
        }

        public void AddTreeItem(string key, T item)
        {
            var path = KeyToTreePath(key);
            var tree = Tree;
            for (var i = 0; i < path.Length; i++)
            {
                if (tree.Nodes == null)
                    tree.Nodes = new TreeNode[MaxTreeLevel];
                if (tree.Nodes[path[i]] == null)
                    tree.Nodes[path[i]] = new TreeNode();
                tree = tree.Nodes[path[i]];
            }

            if (tree.Items == null)
                tree.Items = new List<TreeNodeItems>();
            tree.Items.Add(new TreeNodeItems()
            {
                Item = item,
                Key = key
            });
        }
        public void DeleteTreeItem(string key, T item)
        {
            var path = KeyToTreePath(key);
            var tree = Tree;
            for (var i = 0; i < path.Length; i++)
            {
                if (tree.Nodes == null)
                    return;
                if (tree.Nodes[path[i]] == null)
                    return;
                tree = tree.Nodes[path[i]];
            }
            if (tree.Items == null)
                return;
            for (var i = 0; i < tree.Items.Count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(tree.Items[i].Item, item) && tree.Items[i].Key == key)
                {
                    tree.Items.Remove(tree.Items[i]);
                    i -= 1;
                }
            }

        }

        public IEnumerable<T> SearchTreeItemExactMatch(string key)
        {
            var path = KeyToTreePath(key);
            var tree = Tree;
            for (var i = 0; i < path.Length; i++)
            {
                if (tree.Nodes == null || tree.Nodes[path[i]] == null)
                    yield break;
                tree = tree.Nodes[path[i]];
            }

            if (tree.Items == null)
                yield break;

            for (var i = 0; i < tree.Items.Count; i++)
            {
                if (tree.Items[i].Key == key)
                    yield return tree.Items[i].Item;
            }
        }

        public IEnumerable<T> SearchTreeItem(string filter) // Support for * and ? 
        {
            var posSpecialFilterChar = 0;
            for (; posSpecialFilterChar < filter.Length && filter[posSpecialFilterChar] != '*' && filter[posSpecialFilterChar] != '?'; posSpecialFilterChar++) ;

            if (posSpecialFilterChar == filter.Length)
            {   // No filter used
                foreach (var item in SearchTreeItemExactMatch(filter))
                    yield return item;
                yield break;
            }

            // First do the optimized tree lookup
            var tree = Tree;
            if (posSpecialFilterChar > 0)
            {
                var path = KeyToTreePath(filter.Substring(0, posSpecialFilterChar));
                for (var i = 0; i < path.Length; i++)
                {
                    if (tree.Nodes == null || tree.Nodes[path[i]] == null)
                        yield break;
                    tree = tree.Nodes[path[i]];
                }
            }

            // Now find all items which match (recursive)
            List<TreeNode> Nodes = new List<TreeNode>();
            Nodes.Add(tree);
            HashSet<T> AlreadyFound = new HashSet<T>();
            for (var i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].Items != null)
                {
                    for (var j = 0; j < Nodes[i].Items!.Count; j++)
                    {
                        var item = Nodes[i].Items![j];

                        if (MatchesFilter(item.Key, filter) && !AlreadyFound.Contains(item.Item))
                        {
                            AlreadyFound.Add(item.Item);
                            yield return item.Item;
                        }
                    }
                }

                if (Nodes[i].Nodes != null)
                {
                    for (var j = 0; j < Nodes[i].Nodes!.Length; j++)
                    {
                        if (Nodes[i].Nodes![j] != null)
                            Nodes.Add(Nodes[i].Nodes![j]);
                    }
                }
            }
        }

        public static bool MatchesFilter(string text, string filter)
        {
            var filterPos = 0;
            var lastStartExactMatch = 0;
            var n = 0;
            int inWildcard = 0; // 0 None, 1 ? (Match exact 1 any character), 2 * (Match 0 or more)
            for (; n < text.Length; n++)
            {
                if (filterPos < filter.Length)
                {
                    if (filter[filterPos] == '*')
                    {
                        inWildcard = 2;
                        filterPos++;
                        lastStartExactMatch = filterPos;

                        if (filterPos < filter.Length && filter[filterPos] == text[n])
                            filterPos++;
                    }
                    else if (filter[filterPos] == '?')
                    {
                        filterPos++;
                        inWildcard = 1;
                    }
                    else if (inWildcard == 1) // Match any
                    {
                        filterPos++;
                        inWildcard = 0;
                    }
                    else if (text[n] == filter[filterPos])
                        filterPos++;
                    else if (inWildcard == 0 && text[n] != filter[filterPos])
                        break; // No match
                    else if (inWildcard == 2)
                    {
                        if (filter[filterPos] == text[n])
                            filterPos++;
                        else
                            filterPos = lastStartExactMatch;
                    }

                }
                else if (inWildcard == 1)
                {
                    inWildcard = 0;
                }
                else if (inWildcard == 0)
                    break; // No match                           
            }

            for (; filterPos < filter.Length && filter[filterPos] == '*'; filterPos++) ; // In case there are only *'s left

            return n == text.Length && filterPos == filter.Length;
        }

        private int[] KeyToTreePath(string key)
        {
            List<int> pathItems = new List<int>();
            for (var i = 0; i < key.Length; i += Grouping)
            {
                if (key.Length - i < Grouping)
                    break;
                var part = key.Substring(i, Grouping);
                int number = 0;
                bool putInOtherGroup = false;
                for (var j = 0; j < part.Length; j++)
                {
                    var charValue = part[j];

                    if (charValue > 0xFF || UnlikelyCharacters[charValue] == 255) // Unlikely character
                    {
                        putInOtherGroup = true;
                        break;
                    }

                    var likelyCharIndex = UnlikelyCharacters[charValue];
                    number += likelyCharIndex * (int)Math.Pow(LikelyCharacterCount, j);
                }

                if (putInOtherGroup)
                    pathItems.Add(MaxTreeLevel - 1); // Last level is 'other'
                else
                {
                    number /= Precision; // Todo: See if we can distribute this more evenly 
                    pathItems.Add(number);
                }
            }
            return pathItems.ToArray();
        }

        class TreeNode
        {
            public List<TreeNodeItems>? Items { get; set; }
            public TreeNode[]? Nodes { get; set; }
        }
        class TreeNodeItems
        {
            public required string Key { get; set; }
            public required T Item { get; set; }
        }

    }
}
