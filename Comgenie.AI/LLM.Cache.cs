using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Comgenie.AI
{
    public partial class LLM
    {
		
		private Func<string, bool>? ExistsInCacheHandler { get; set; }
		private Func<string, Task<string>>? ReadFromCacheHandler { get; set; }
		private Action<string, string>? UpdateCacheHandler { get; set; }


		/// <summary>
		/// Custom cache handling.
		/// </summary>
		/// <param name="existsInCacheHandler">Function which should return true if the given parameter (key) is found in the cache</param>
		/// <param name="readFromCacheHandler">Function which returns a stream to access an item found in the cache.</param>
		/// <param name="updateCacheHandler">Action to update cache for key (first parameter) with the contents of the stream.</param>
		public void SetCache(Func<string, bool> existsInCacheHandler, Func<string, Task<string>> readFromCacheHandler, Action<string, string> updateCacheHandler)
		{
			ExistsInCacheHandler = existsInCacheHandler;
			ReadFromCacheHandler = readFromCacheHandler;
			UpdateCacheHandler = updateCacheHandler;
		}

		/// <summary>
		/// Uses Comgenie.Util.ArchiveFile to store the cache in a file + index file. This automatically sets all the correct handlers.
		/// </summary>
		/// <param name="fileName">Name of the archive file to store the cache in</param>
		public void SetCache(string fileName)
		{
			var archiveFile = new Comgenie.Util.ArchiveFile(fileName);
			ExistsInCacheHandler = (key) => archiveFile.Exists(key);
			ReadFromCacheHandler = async (key) =>
			{
				using var stream = await archiveFile.Open(key);
				using var reader = new StreamReader(stream!); // We assume it's never null as this method is only called if the key exists
				var txt = await reader.ReadToEndAsync();
				return txt;
			};
			UpdateCacheHandler = async (key, content) =>
			{
				using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
				await archiveFile.Add(key, memoryStream);
			};

		}

		private static string CalculateHash(string text)
		{
			using (var sha256 = SHA256.Create())
			{
				var bytes = Encoding.UTF8.GetBytes(text);
				var hashBytes = sha256.ComputeHash(bytes);
				return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
			}
		}
	}
}
