using System;
using System.IO;
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
        /// </summary>
        public static class ServiceProviderBuilder
        {
            public static IServiceProvider GetServiceProvider()
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", true, true)
                    .AddEnvironmentVariables()
                    //.AddUserSecrets(typeof(RESTTopo).Assembly)
                    //.AddUserSecrets<HeronConfiguration>(true,true)
                    .AddUserSecrets("6aefc65d-9849-4d4f-b9a7-16d77517db86")
                    .Build();
                var services = new ServiceCollection();

                services.Configure<HeronConfiguration>(configuration.GetSection(typeof(HeronConfiguration).FullName));

                var provider = services.BuildServiceProvider();
                return provider;
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