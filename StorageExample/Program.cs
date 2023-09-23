using Comgenie.Storage;
using Comgenie.Storage.Entities;
using Comgenie.Storage.Locations;
using Comgenie.Storage.Utils;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;

namespace StorageExample
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // QueryTest();   Not fully implemented yet

            EncryptedAndRepairableStreamExample();
            StoragePoolTest().Wait();
        }
        static async void QueryTest()
        {
            var test = new QueryTranslator<StorageItem>(new Func<string, IEnumerable<StorageItem>>((filter) =>
            {
                Console.WriteLine("Retrieving items for filter: " + filter);
                var list = new List<StorageItem>();
                list.Add(new StorageItem()
                {
                    Id = "123",
                    Created = DateTime.Now
                });
                return list;
            }));


            var test2 = test.Where(a => a.Id == "12\'3" && (a.Created > new DateTime(2010, 01, 01, 0, 0, 0, 0, DateTimeKind.Local) || a.Created < new DateTime(2020, 01, 01, 0, 0, 0, 0, DateTimeKind.Utc)));
            var test3 = test2.ToList();
        }

        static async Task StoragePoolTest()
        {
            // Prepare
            if (Directory.Exists(".\\folder-1"))
                Directory.Delete(".\\folder-1", true);
            Directory.CreateDirectory(".\\folder-1");

            if (Directory.Exists(".\\folder-2"))
                Directory.Delete(".\\folder-2", true);
            Directory.CreateDirectory(".\\folder-2");

            if (File.Exists("archive.zip"))
                File.Delete("archive.zip");

            using (var sp = new StoragePool())
            {
                // folder-1 is used as a live storage location with the highest priority
                // all file access is initially done here
                await sp.AddStorageLocationAsync(new DiskStorageLocation(".\\folder-1"), Encoding.UTF8.GetBytes("encryption key"));

                // folder-2 is a backup storage which we use to sync changes to and from
                // by setting the shared setting to true, this location can be used by multiple clients at the same time (a lock mechanism will be used)
                await sp.AddStorageLocationAsync(new DiskStorageLocation(".\\folder-2"), Encoding.UTF8.GetBytes("other key"), syncInterval: 60, shared: false, priority: 2, enableRepairData: true);

                // Azure blob storage location, to test you'll need a SAS url with write access
                //var azure = new AzureBlobStorageLocation("https://YOUR-SAS-URL-HERE");
                //await sp.AddStorageLocationAsync(azure, Encoding.UTF8.GetBytes("another encryption key"), syncInterval: 60, shared: false, priority: 10, repairPercent: 10);

                using (var file = sp.Open("testfile.txt", FileMode.Create, FileAccess.Write, new string[] { "TestTag=123"}))
                using (var writer = new StreamWriter(file))
                {
                    await writer.WriteLineAsync("blah blah blah");
                }

                // Retrieve all files from the storage pool with a filter starting with "test"
                Console.WriteLine("Retrieving all files matching the filter Test*");
                foreach (var file in sp.List("Test*"))
                {
                    Console.WriteLine("- "+ file.Id + ", Tags: " + string.Join(", " , file.Tags)+", Size: " + file.Length);
                }
                Console.WriteLine("Unloading first storage pool");
            }

            // Only load backup location
            Console.WriteLine("Only loading backup location");
            using (var sp = new StoragePool())
            {
                await sp.AddStorageLocationAsync(new DiskStorageLocation(".\\folder-2"), Encoding.UTF8.GetBytes("other key"), syncInterval: 60, priority: 2, enableRepairData: true);

                // Retrieve all files from the storage pool with a filter starting with "test"
                Console.WriteLine("Retrieving all files matching the filter Test*");
                foreach (var file in sp.List("Test*"))
                {
                    Console.WriteLine("- " + file.Id + ", Tags: " + string.Join(", ", file.Tags) + ", Size: " + file.Length);
                    using (var stream = sp.Open(file.Id, FileMode.Open, FileAccess.Read))
                    using (var reader = new StreamReader(stream))
                    {
                        Console.WriteLine("Contents: " + reader.ReadToEnd());
                    }
                }


                Console.WriteLine("Delete test file");
                await sp.DeleteAsync("testfile.txt");

                Console.WriteLine("Files matching the filter Test* : " + sp.List("Test*").ToList().Count);
            }
        }


        static void EncryptedAndRepairableStreamExample()
        {
            // Prepare
            if (File.Exists("testdata.txt"))
                File.Delete("testdata.txt");

            // Write test data with repair data
            var testLineCount = 1000;
            using (var file = File.OpenWrite("testdata.txt"))
            using (var stream = new EncryptedAndRepairableStream(file, ASCIIEncoding.UTF8.GetBytes("encryption key"), true))
            using (var writer = new StreamWriter(stream))
            {
                for (var i = 0; i < testLineCount; i++)
                    writer.WriteLine("Test line");
            }

            // Corrupt file            
            using (var file = File.OpenWrite("testdata.txt"))
            {
                file.Seek(100, SeekOrigin.Begin);
                var testData = ASCIIEncoding.UTF8.GetBytes("BLAHBLAHBLAHBLAHBLAH");
                file.Write(testData, 0, testData.Length);
                file.Flush();
            }

            // Repair, uncomment this to repair the file on disk, otherwise the file will be repaired on the fly when reading
            /*using (var file = File.Open("testdata.txt", FileMode.Open))
            using (var stream = new EncryptedAndRepairableStream(file, ASCIIEncoding.UTF8.GetBytes("encryption key"), true))
            {
                stream.Repair();
            }*/

            // Test
            using (var file = File.OpenRead("testdata.txt"))
            using (var stream = new EncryptedAndRepairableStream(file, ASCIIEncoding.UTF8.GetBytes("encryption key"), true))
            using (var reader = new StreamReader(stream))
            {
                // Seeks are also supported
                // stream.Seek(12, SeekOrigin.Begin);

                var actualLineCount = 0;
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                        break;
                    if (line != "Test line")
                        Console.WriteLine("FAIL! " + line);
                    actualLineCount++;
                }
                if (actualLineCount != testLineCount)
                    Console.WriteLine("Line count fail, " + testLineCount+" != " + actualLineCount);
            }
        }
    }
}