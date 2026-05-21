using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PropFirmGuardian.Utils
{
    public static class EncryptionHelper
    {
        private static readonly byte[] Salt = Encoding.UTF8.GetBytes("PFG2026Salt");

        public static byte[] Encrypt(string plainText, string key)
        {
            if (plainText == null)
                plainText = string.Empty;

            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = DeriveKey(key);
                aes.GenerateIV();

                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] cipherBytes;

                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                    cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

                byte[] output = new byte[aes.IV.Length + cipherBytes.Length];
                Buffer.BlockCopy(aes.IV, 0, output, 0, aes.IV.Length);
                Buffer.BlockCopy(cipherBytes, 0, output, aes.IV.Length, cipherBytes.Length);
                return output;
            }
        }

        public static string Decrypt(byte[] encryptedData, string key)
        {
            if (encryptedData == null || encryptedData.Length <= 16)
                throw new ArgumentException("Encrypted data is missing or invalid.", "encryptedData");

            byte[] iv = new byte[16];
            byte[] cipherBytes = new byte[encryptedData.Length - iv.Length];
            Buffer.BlockCopy(encryptedData, 0, iv, 0, iv.Length);
            Buffer.BlockCopy(encryptedData, iv.Length, cipherBytes, 0, cipherBytes.Length);

            using (Aes aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = DeriveKey(key);
                aes.IV = iv;

                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                {
                    byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                    return Encoding.UTF8.GetString(plainBytes);
                }
            }
        }

        public static void EncryptToFile(string plainText, string filePath, string key)
        {
            byte[] encrypted = Encrypt(plainText, key);
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(filePath, encrypted);
        }

        public static string DecryptFromFile(string filePath, string key)
        {
            byte[] encrypted = File.ReadAllBytes(filePath);
            return Decrypt(encrypted, key);
        }

        private static byte[] DeriveKey(string key)
        {
            if (key == null)
                key = string.Empty;

            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] material = new byte[keyBytes.Length + Salt.Length];
            Buffer.BlockCopy(keyBytes, 0, material, 0, keyBytes.Length);
            Buffer.BlockCopy(Salt, 0, material, keyBytes.Length, Salt.Length);

            using (SHA256 sha256 = SHA256.Create())
                return sha256.ComputeHash(material);
        }
    }
}
