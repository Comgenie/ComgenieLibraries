using Comgenie.Util;
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
    /// <summary>
    /// Simple utility class to provide an encrypted Key/Value storage.
    /// </summary>
    public class KeyStore
    {
        private Dictionary<string, string?> Data { get; set; } = new();
        private string FileName { get; set; }
        private bool FileNameSpecified { get; set; }
        private byte[] EncryptionKey { get; set; }

        public KeyStore(string fileName, byte[] encryptionKey, bool fileNameSpecified=false)
        {
            FileName = fileName;
            EncryptionKey = encryptionKey;
            FileNameSpecified = fileNameSpecified;
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
                if (!Directory.Exists(GlobalConfiguration.SecretsFolder))
                    Directory.CreateDirectory(GlobalConfiguration.SecretsFolder);

                var hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(identity));
                fileName = Path.Combine(GlobalConfiguration.SecretsFolder, "key-" + Convert.ToHexString(hash));
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
            if (!Directory.Exists(GlobalConfiguration.SecretsFolder))
                return false;
            identity = identity.ToLower();

            var hash = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(identity));
            var fileName = Path.Combine(GlobalConfiguration.SecretsFolder, "key-" + Convert.ToHexString(hash));
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
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
                    if (dict == null)
                        return false;
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
                FileName = Path.Combine(GlobalConfiguration.SecretsFolder, "key-" + Convert.ToHexString(hash));

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
