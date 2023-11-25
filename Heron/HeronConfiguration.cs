using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Heron
{
    class HeronConfig
    {
        /// <summary>
        /// Enable use of UserSecrets in order to keep keys/tokens off Github.  Before compiling copy appsettings.json to 
        /// \AppData\Roaming\Microsoft\UserSecrets\6aefc65d-9849-4d4f-b9a7-16d77517db86 and rename as secrets.json.
        /// Edit secrets.json with your own keys/tokens.
        /// Based on this approach:
        /// https://www.frakkingsweet.com/add-usersecrets-to-net-core-console-application/
        /// </summary>
        public static class ServiceProviderBuilder
        {
            public static IServiceProvider GetServiceProvider()
            {
                ///Put your secrets in the following AppData UserSecret folder
                ///Use appsetttings.json as a template for secrets.json
                var ghLibFile = typeof(ImportVectorSRS).Assembly.Location;
                var executingDirectory = Path.GetDirectoryName(ghLibFile);
                string appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string userSecretsFilePath = Path.Combine(appDataFolder,"Microsoft","UserSecrets","6aefc65d-9849-4d4f-b9a7-16d77517db86","secrets.json");
                string appsettingsEncrypted = Path.Combine(executingDirectory, "HeronAppSettings.json");

                ///Encrypt secrets.json on compiling machine.  Only put public keys in these secrets, not any that are should really be kept secret.
                ///In theory appsettingsEncrypt.json file can get decrypted with the key below, but if someone tries this hard, they can have the pulbic secrets.
                ///The main purpose here is to avoid committing readable secrets to GitHub.
                if (File.Exists(userSecretsFilePath))
                {
                    EncryptAppSettings(userSecretsFilePath, appsettingsEncrypted);
                }

                ///Read secrets from appsettingsEncrypted.json which is what you should ship with the compiled version of Heron
                ///appsettings.json is included in GitHub to be used as a template
                Stream jsonStream = DecryptAppSettings(appsettingsEncrypted);

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(executingDirectory)
                    .AddJsonFile("appsettings.json", true, true)
                    .AddEnvironmentVariables()
                    //.AddUserSecrets("6aefc65d-9849-4d4f-b9a7-16d77517db86")
                    .AddJsonStream(jsonStream)
                    .Build();
                var services = new ServiceCollection();

                services.Configure<HeronConfiguration>(configuration.GetSection(typeof(HeronConfiguration).FullName));

                var provider = services.BuildServiceProvider();
                return provider;
            }

            /// <summary>
            /// Enrcypt a string from a file path of fileName
            /// </summary>
            /// <param name="fileName"></param>
            /// <param name="fileNameEncrypted"></param>
            /// <returns></returns>
            public static bool EncryptAppSettings (string fileName, string fileNameEncrypted)
            {
                ///From https://docs.microsoft.com/en-us/dotnet/standard/security/encrypting-data
                bool success = false;

                try
                {
                    using (FileStream fileStream = new FileStream(fileNameEncrypted, FileMode.OpenOrCreate))
                    {
                        using (Aes aes = Aes.Create())
                        {
                            byte[] key =
                            {
                                0x16, 0x02, 0x14, 0x04, 0x12, 0x06, 0x10, 0x08,
                                0x09, 0x07, 0x11, 0x05, 0x13, 0x03, 0x15, 0x01
                            };
                            aes.Key = key;

                            byte[] iv = aes.IV;
                            fileStream.Write(iv, 0, iv.Length);

                            using (CryptoStream cryptoStream = new CryptoStream(
                                fileStream,
                                aes.CreateEncryptor(),
                                CryptoStreamMode.Write))
                            {
                                using (StreamWriter encryptWriter = new StreamWriter(cryptoStream))
                                {       
                                    encryptWriter.WriteLine(File.ReadAllText(fileName));
                                }
                            }
                        }
                        success = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
                return success;
            }
        
            /// <summary>
            /// Decrypt a string from a file path of fileName
            /// </summary>
            /// <param name="fileName"></param>
            /// <returns></returns>
            public static Stream DecryptAppSettings (string fileName)
            {
                ///From https://docs.microsoft.com/en-us/dotnet/standard/security/decrypting-data
                Stream jsonStream = null;

                try
                {
                    using (FileStream fileStream = new FileStream(fileName, FileMode.Open))
                    {
                        using (Aes aes = Aes.Create())
                        {
                            byte[] iv = new byte[aes.IV.Length];
                            int numBytesToRead = aes.IV.Length;
                            int numBytesRead = 0;
                            while (numBytesToRead > 0)
                            {
                                int n = fileStream.Read(iv, numBytesRead, numBytesToRead);
                                if (n == 0) break;

                                numBytesRead += n;
                                numBytesToRead -= n;
                            }

                            byte[] key =
                            {
                                0x16, 0x02, 0x14, 0x04, 0x12, 0x06, 0x10, 0x08,
                                0x09, 0x07, 0x11, 0x05, 0x13, 0x03, 0x15, 0x01
                            };

                            using (CryptoStream cryptoStream = new CryptoStream(
                               fileStream,
                               aes.CreateDecryptor(key, iv),
                               CryptoStreamMode.Read))
                            {
                                using (StreamReader decryptReader = new StreamReader(cryptoStream))
                                {
                                    string decryptedMessage = decryptReader.ReadToEnd();
                                    var stream = new MemoryStream();
                                    var writer = new StreamWriter(stream);
                                    writer.Write(decryptedMessage);
                                    writer.Flush();
                                    stream.Position = 0;
                                    jsonStream = stream;
                                    Console.WriteLine($"The decrypted original message: {decryptedMessage}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }

                return jsonStream;
            }
        }

        /// <summary>
        /// Call LoadKeys() to load in secrets 
        /// </summary>
        public static void LoadKeys()
        {
            
            var services = Heron.HeronConfig.ServiceProviderBuilder.GetServiceProvider();
            var options = services.GetRequiredService<IOptions<HeronConfiguration>>();

            OpenTopographyAPIKey = options.Value.HeronOpenTopographyAPI;
        }

        public static string OpenTopographyAPIKey { get; set; }

    }
    public class HeronConfiguration
    {
        public string HeronOpenTopographyAPI { get; set; }

    }


}