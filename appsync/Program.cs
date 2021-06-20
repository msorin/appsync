using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Amazon.S3;
using Microsoft.Extensions.Configuration;

namespace appsync
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                throw new InvalidOperationException("No environment was specified.");
            }

            var builder = new ConfigurationBuilder()
                   .SetBasePath(Directory.GetCurrentDirectory())
                   .AddJsonFile("appsettings.json", optional: false)
                   .AddJsonFile($"appsettings.{args[0]}.json", optional: false);

            var config = builder.Build();

            var target = config.GetSection("Target").Get<Target>();
            var iisApplication = config.GetSection("IISApplication").Get<IISApplication>();

            var unset = target.GetUnsetStrings().Concat(iisApplication.GetUnsetStrings());

            if (unset.Any())
            {
                throw new InvalidOperationException($"The following settings were not set in configuration: '{string.Join("', '", unset)}'.");
            }

            var dir = config.GetSection("Temp").Value ?? Path.GetTempPath();
            var s3 = new AmazonS3Client();
            var response = await s3.ListObjectsAsync(target.Bucket, target.Path);

            var latest = response.S3Objects.OrderBy(x => x.LastModified).Last();
            var output = Path.Combine(dir, $"{latest.Key.Split('/').Last()}.zip");

            if (File.Exists(output))
            {
                Console.WriteLine($"File {output} already exists, no updates needed for {args[0]}");
                return;
            }

            var zip = await s3.GetObjectAsync(target.Bucket, latest.Key);
            await zip.WriteResponseStreamToFileAsync(output, false, default);

            var folderDialog = Process.Start(new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = output
            });
        }
    }
}
