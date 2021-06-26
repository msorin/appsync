using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Web.Administration;
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
            var logRoot = config.GetSection("Log").Value ?? dir;
            BuildDirectory(logRoot);
            using var file = new StreamWriter(File.OpenWrite(Path.Combine(logRoot, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log")))
            {
                AutoFlush = true
            };
            Action<string> log = (s) =>
            {
                Console.WriteLine(s);
                file.WriteLine(s);
            };
            using var s3 = new AmazonS3Client();
            var response = await s3.ListObjectsAsync(target.Bucket, target.Path);

            var latest = response.S3Objects.OrderBy(x => x.LastModified).Last();
            var version = latest.Key.Split('/').Last();
            var output = Path.Combine(dir, $"{version}.zip");

            if (File.Exists(output))
            {
                log($"File {output} already exists, no updates needed for {args[0]}");
                return;
            }
            log($"Getting {target.Bucket}/{latest.Key} from s3");
            var zip = await s3.GetObjectAsync(target.Bucket, latest.Key);
            await zip.WriteResponseStreamToFileAsync(output, false, default);
            log($"'{output}' file created");

            //move folder to target
            var extractPath = Path.Combine(iisApplication.RootPath, iisApplication.LiveFolder, version);
            var livePath = Path.Combine(iisApplication.RootPath, iisApplication.LiveFolder);
            ZipFile.ExtractToDirectory(output, extractPath);
            log($"'{extractPath}' extracted.");
            var manager = new ServerManager();
            var pool = manager.ApplicationPools.First(x => x.Name.Equals(iisApplication.ApplicationPool, StringComparison.InvariantCultureIgnoreCase));
            log($"Attempting to stop Application Pool: {pool.Name}");
            pool.Stop();
            log($"Stopped Application Pool: {pool.Name}");

            Directory.Move(livePath, $"{livePath}_");
            log($"Moved '{livePath}' => '{livePath}_'");
            Directory.Move(extractPath, livePath);
            log($"Moved '{extractPath}' => '{livePath}'");

            log($"Attempting to start Application Pool: {pool.Name}");
            pool.Start();
            log($"Started Application Pool: {pool.Name}");
        }

        private static void BuildDirectory(string root, string sub = null)
        {
            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }

            if (!string.IsNullOrWhiteSpace(sub))
            {
                BuildDirectory(Path.Combine(root, sub));
            }
        }
    }
}
