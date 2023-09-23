using Comgenie.Storage.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Comgenie.Storage
{
    public class KeyStore
    {
        private Dictionary<string, string> Data { get; set; }
        private string FileName { get; set; }
        private bool FileNameSpecified { get; set; }
        private byte[] EncryptionKey { get; set; }

        public KeyStore(string fileName, byte[] encryptionKey, bool fileNameSpecified=false)
        {
            FileName = fileName;
            EncryptionKey = encryptionKey;
            FileNameSpecified = fileNameSpecified;
            Data = new Dictionary<string, string>();
        }

        public static KeyStore? Get(string identity, string password, string? fileName = null, bool createIfNotExists = false)
        {
            if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(password))
                return null;
            identity = identity.ToLower();
            var encryptionKey = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(identity + "\0" + password));

            var fileNameSpecified = true;
            if (string.IsNullOrEmpty(fileName))
            {
                fileNameSpecified = false;
                if (!Directory.Exists("Keys"))
                    Directory.CreateDirectory("Keys");

                var hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(identity));
                fileName = Path.Combine("Keys", "key-" + Convert.ToHexString(hash));
            }

            var keyStore = new KeyStore(fileName, encryptionKey, fileNameSpecified);

            if (!File.Exists(fileName))
            {
                if (!createIfNotExists)
                    return null;
                keyStore.Save();                
            }
            else
            {
                if (!keyStore.Reload())
                    return null;
            }
            return keyStore;
        }
        public static bool Exists(string identity)
        {
            if (!Directory.Exists("Keys"))
                return false;
            identity = identity.ToLower();

            var hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(identity));
            var fileName = Path.Combine("Keys", "key-" + Convert.ToHexString(hash));
            return File.Exists(fileName);
        }

        public string? this[string name]
        {
            get
            {
                if (!Data.ContainsKey(name))
                    return null;
                return Data[name];
            }
            set
            {
                Data[name] = value;
            }
        }
        private const string VerifyLine = "Encrypted verify line";
        public bool Reload()
        {
            try
            {
                using (var file = File.Open(FileName, FileMode.Open, FileAccess.Read))
                using (var es = new EncryptedAndRepairableStream(file, EncryptionKey, true))
                using (var reader = new StreamReader(es))
                {
                    var verifyLine = reader.ReadLine();
                    if (verifyLine != VerifyLine)
                        return false;

                    var json = reader.ReadToEnd();
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    Data = dict;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
        /// <summary>
        /// Set a new encryption key based on the given identity and password. This will also save the current changes.
        /// </summary>
        /// <param name="identity"></param>
        /// <param name="password"></param>
        public void SetPassword(string identity, string password)
        {
            identity = identity.ToLower();
            EncryptionKey = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(identity + "\0" + password));
            if (!FileNameSpecified)
            {
                var oldFileName = FileName;
                var hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(identity));
                FileName = Path.Combine("Keys", "key-" + Convert.ToHexString(hash));

                if (oldFileName != FileName)
                {
                    // Identity changed and therefor filename changed. We will delete the old file after saving the new one
                    Save();
                    if (File.Exists(oldFileName))
                        File.Delete(oldFileName);
                    return;
                }
            }
            Save();
        }

        /// <summary>
        /// Store any changes to the encrypted identity file
        /// </summary>
        public void Save()
        {
            var json = JsonSerializer.Serialize(Data);
            using (var file = File.Open(FileName, FileMode.Create))
            using (var es = new EncryptedAndRepairableStream(file, EncryptionKey, true))
            using (var writer = new StreamWriter(es))
            {
                writer.WriteLine(VerifyLine);
                writer.Write(json);
            }
        }
    }
}
