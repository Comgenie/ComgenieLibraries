namespace Comgenie.AI
{
    // Additional helper to make working with the VectorDB easier for documents.
    public class DocumentVectorDB : VectorDB<DocumentVectorDB.DocumentSourceReference>
    {
        public Dictionary<string, DocumentSource> Documents { get; private set; } = new();
        public DocumentVectorDB(int dimension) : base(dimension) { }
        public DocumentSource UpsertDocumentSource(string sourceName, string documentText, bool removeExistingReferences = true)
        {
            if (!Documents.ContainsKey(sourceName))
            {
                Documents[sourceName] = new DocumentSource
                {
                    SourceName = sourceName,
                    Text = documentText
                };
            }

            var document = Documents[sourceName];

            if (removeExistingReferences)
            {
                foreach (var reference in document.References)
                {
                    Delete(reference);
                }
            }

            return document;
        }
        public bool DeleteDocumentSource(string sourceName)
        {
            if (!Documents.ContainsKey(sourceName))
                return false;

            var document = Documents[sourceName];

            foreach (var reference in document.References)
                Delete(reference);

            return true;
        }
        public void UpsertDocumentSection(DocumentSource documentSource, int offset, int count, float[] vector)
        {
            var sourceRef = new DocumentSourceReference
            {
                Source = documentSource,
                Offset = offset,
                Length = count,
                DocumentReferenceIndex = documentSource.References.Count
            };

            documentSource.References.Add(sourceRef);

            Upsert(sourceRef, vector);
        }

        public List<ScoredItem<DocumentSourceReference>> CombineCloseResults(List<ScoredItem<DocumentSourceReference>> items, int margin = 0)
        {
            var list = new List<ScoredItem<DocumentSourceReference>>();

            // Combine overlapping and close (within margin) items. Keep the highest similarity score.
            foreach (var item in items.OrderBy(i => i.Item.Offset))
            {
                var sameDocumentItems = list.Where(a => a.Item.Source == item.Item.Source).ToList();
                if (sameDocumentItems.Count == 0)
                {
                    list.Add(item);
                    continue;
                }
                var last = sameDocumentItems.Last();
                if (item.Item.Offset <= last.Item.Offset + last.Item.Length + margin)
                {
                    // Overlapping or close, combine
                    var newOffset = Math.Min(last.Item.Offset, item.Item.Offset);
                    var newEnd = Math.Max(last.Item.Offset + last.Item.Length, item.Item.Offset + item.Item.Length);
                    var newLength = newEnd - newOffset;
                    var newText = item.Item.Source.Text.AsMemory(newOffset, newLength);
                    var newSimilarity = Math.Max(last.Score, item.Score);
                    var combinedKey = new DocumentSourceReference
                    {
                        Source = item.Item.Source,
                        Offset = newOffset,
                        Length = newLength,
                        DocumentReferenceIndex = -1 // Not relevant for combined
                    };

                    list[list.Count - 1] = new ScoredItem<DocumentSourceReference>()
                    {
                        Item = combinedKey,
                        Score = newSimilarity
                    };
                }
                else
                {
                    list.Add(item);
                }
            }

            return list;
        }

        public class DocumentSource
        {
            public required string SourceName { get; set; }
            public required string Text { get; set; }
            public List<DocumentSourceReference> References { get; set; } = new();


        }
        public struct DocumentSourceReference
        {
            public DocumentSource Source { get; set; }
            public int Offset { get; set; }
            public int Length { get; set; }
            public int DocumentReferenceIndex { get; set; }

            public ReadOnlyMemory<char> GetTextSection()
            {
                return Source.Text.AsMemory(Offset, Length);
            }

            // Required for the methods within LLM.Embeddings to work correctly
            public override string ToString()
            {
                return Source.Text.Substring(Offset, Length);
            }
        }
    }
}
