using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.AI
{
    // Note: This class is mostly AI generated code.
    
    public class VectorDB<T> : IDisposable where T : notnull
    {
        public readonly int VectorDimension;

        // Internal storage structure
        private record VectorItem(T Key, ReadOnlyMemory<char> Text, float[] Vector, float Magnitude);

        private readonly Dictionary<T, VectorItem> _storage;
        private readonly ReaderWriterLockSlim _lock;

        /// <summary>
        /// Initializes the database with a fixed vector dimension.
        /// </summary>
        /// <param name="dimension">The size of the float array for all vectors.</param>
        public VectorDB(int dimension)
        {
            if (dimension <= 0) throw new ArgumentException("Dimension must be greater than 0");

            VectorDimension = dimension;
            _storage = new Dictionary<T, VectorItem>();
            _lock = new ReaderWriterLockSlim();
        }

        /// <summary>
        /// Adds or Updates a vector entry.
        /// </summary>
        public void Upsert(T key, ref string text, float[] vector)
        {
            Upsert(key, text.AsMemory(), vector);
        }
        public void Upsert(T key, string text, float[] vector)
        {
            Upsert(key, text.AsMemory(), vector);
        }

        public void Upsert(T key, ReadOnlyMemory<char> text, float[] vector)
        {
            if (vector.Length != VectorDimension)
                throw new ArgumentException($"Vector size must be {VectorDimension}");

            // Pre-calculate magnitude to avoid doing it inside the search loop (Optimization)
            float magnitude = CalculateMagnitude(vector);

            var item = new VectorItem(key, text, vector, magnitude);

            _lock.EnterWriteLock();
            try
            {
                _storage[key] = item;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Removes an item by Key.
        /// </summary>
        public bool Delete(T key)
        {
            _lock.EnterWriteLock();
            try
            {
                return _storage.Remove(key);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Finds the nearest vectors to the query vector using Cosine Similarity.
        /// </summary>
        /// <param name="queryVector">The input vector to compare against.</param>
        /// <param name="limit">Number of results to return.</param>
        /// <returns>List of matches ordered by similarity (1.0 is identical, -1.0 is opposite).</returns>
        public List<ScoredItem<T>> Search(float[] queryVector, int limit)
        {
            var results = new List<ScoredItem<T>>();

            if (queryVector.Length != VectorDimension)
                throw new ArgumentException($"Query vector dimensions mismatch. Expected {VectorDimension}.");

            float queryMag = CalculateMagnitude(queryVector);

            // Optimization: If query vector is zero-length, similarity is undefined/zero.
            if (queryMag == 0)
                return results;            

            // Use a PriorityQueue to keep track of top K only.
            // We use a Min-Heap behavior: priority is the Score.
            // If we find a score higher than the lowest in the queue, we swap.
            // Note: PriorityQueue is available in .NET 6+.
            var pq = new PriorityQueue<VectorItem, float>();

            _lock.EnterReadLock();
            try
            {
                foreach (var item in _storage.Values)
                {
                    // 1. Calculate Dot Product (SIMD optimized)
                    float dotProduct = DotProductSimd(queryVector, item.Vector);

                    // 2. Calculate Cosine Similarity
                    // Cosine = (A . B) / (||A|| * ||B||)
                    // We use the pre-calculated magnitude for the stored item.
                    float similarity = 0f;
                    if (item.Magnitude > 0 && queryMag > 0)
                    {
                        similarity = dotProduct / (item.Magnitude * queryMag);
                    }

                    // 3. Maintain Top-K Heap
                    if (pq.Count < limit)
                    {
                        pq.Enqueue(item, similarity);
                    }
                    else
                    {
                        pq.TryPeek(out _, out float lowestScore);

                        if (similarity > lowestScore)
                        {
                            pq.Dequeue();
                            pq.Enqueue(item, similarity);
                        }
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            // Unload the Queue
            // Since it's a Min-Heap (lowest score at root), dequeuing gives us ascending order.
            // We want descending (highest similarity first), so we insert at 0 or Reverse later.
            while (pq.Count > 0)
            {
                if (pq.TryDequeue(out VectorItem? item, out float score))
                {
                    results.Add(new ScoredItem<T>()
                    {
                        Item = item.Key,
                        Score = score
                    });
                }
            }

            results.Reverse(); // Sort Highest -> Lowest
            return results;
        }

        /// <summary>
        /// Calculates Dot Product using SIMD (Single Instruction Multiple Data) via System.Numerics.
        /// </summary>
        private static float DotProductSimd(float[] a, float[] b)
        {
            int vecSize = Vector<float>.Count;
            var accVector = Vector<float>.Zero;
            int i = 0;

            // Process main chunk using hardware vectors (AVX/SSE)
            for (; i <= a.Length - vecSize; i += vecSize)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);
                accVector += va * vb;
            }

            // Sum the vector elements
            float result = Vector.Dot(accVector, Vector<float>.One);

            // Process remaining elements (if array length is not multiple of vector size)
            for (; i < a.Length; i++)
            {
                result += a[i] * b[i];
            }

            return result;
        }

        /// <summary>
        /// Calculates Euclidean Norm (Magnitude).
        /// </summary>
        private static float CalculateMagnitude(float[] vector)
        {
            // Magnitude = Sqrt(Sum(x^2))
            // We can reuse the DotProduct logic: Dot(v, v) = Sum(x^2)
            float sumSquares = DotProductSimd(vector, vector);
            return (float)Math.Sqrt(sumSquares);
        }

        public IEnumerable<(T Key, float[] Vector)> AsEnumerable()
        {
            _lock.EnterReadLock();
            try
            {
                foreach (var item in _storage.Values)
                {
                    yield return (item.Key, item.Vector);
                }
            }
            finally
            { 
                _lock.ExitReadLock();
            }
            yield break;
        }

        public void Dispose()
        {
            _lock?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
    public class VectorDB : VectorDB<string>
    {
        public VectorDB(int dimension) : base(dimension) { }
    }

    public class ScoredItem<T>
    {
        public required T Item { get; set; }
        public required float Score { get; set; }
    }
}
