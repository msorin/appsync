using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Web.Administration;
using Slack.Webhooks;
namespace appsync
{
    class Program
    {
        private static TimeSpan _logRetention = TimeSpan.FromDays(15);
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
            var dateStamp = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            var dir = config.GetSection("Temp").Value ?? Path.GetTempPath();
            var logRoot = config.GetSection("Log").Value ?? dir;

            BuildDirectory(logRoot, args[0]);
            var work = new List<Task>();
            var logDir = new DirectoryInfo(Path.Combine(logRoot, args[0]));

            using var file = new StreamWriter(File.OpenWrite(Path.Combine(logRoot, args[0], $"{dateStamp}.log")))
            {
                AutoFlush = true
            };
            Action<string> log = (s) =>
            {
                Console.WriteLine(s);
                file.WriteLine($"{DateTime.Now:o} | {s}");
            };
            if (logDir.Exists)
            {
                work.Add(Task.Run(() =>
                {
                    try
                    {
                        var logs = logDir.GetFiles();
                        foreach (var file in logs)
                        {
                            if (file.CreationTime + _logRetention < DateTime.Today)
                            {
                                file.Delete();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log($"{ex}");
                    }
                }));
            }
            work.Add(Task.Run(async () =>
            {
                try
                {
                    using var s3 = new AmazonS3Client();
                    using var cli = new SlackClient(config.GetSection("AwsChannel").Value);
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
                    var extractPath = Path.Combine(iisApplication.RootPath, $"{iisApplication.LiveFolder}_{version}");
                    var livePath = Path.Combine(iisApplication.RootPath, iisApplication.LiveFolder);
                    if (Directory.Exists(extractPath))
                    {
                        Directory.Delete(extractPath, true);
                    }
                    ZipFile.ExtractToDirectory(output, extractPath);
                    log($"'{extractPath}' extracted.");
                    var manager = new ServerManager();

                    log($"Application pools currently active:");
                    await Task.WhenAll(manager.ApplicationPools.Select(x => Task.Run(() => log(x.Name))));
                    log($"Locating {iisApplication.ApplicationPool} application pool");

                    var pool = manager.ApplicationPools.First(x => x.Name.Equals(iisApplication.ApplicationPool, StringComparison.InvariantCultureIgnoreCase));
                    while (pool.State != ObjectState.Stopped)
                    {
                        if (pool.State == ObjectState.Started)
                        {
                            log($"Attempting to stop Application Pool: {pool.Name}");
                            pool.Stop();
                        }

                        log($"Stopping Application Pool: {pool.Name}...");
                        await Task.Delay(500);
                    }

                    log($"Stopped Application Pool: {pool.Name}");
                    async Task performMove(int attempt)
                    {
                        try
                        {
                            Directory.Move(livePath, $"{livePath}_{dateStamp}");
                            log($"Moved '{livePath}' => '{livePath}_{dateStamp}'");
                            Directory.Move(extractPath, livePath);
                            log($"Moved '{extractPath}' => '{livePath}'");
                        }
                        catch (Exception ex)
                        {
                            log($"{ex}");
                            await Task.Delay(500);

                            if (attempt < 10)
                            {
                                await performMove(++attempt);
                                return;
                            }

                            await cli.PostAsync(new SlackMessage
                            {
                                Markdown = true,
                                Text = $":this-is-fine: {args[0]} FAILED TO DEPLOY (action required) :this-is-fine:",
                            });

                            log($"Deleting file '{output})' to retry");
                            File.Delete(output);
                            await StartPool(1);
                            throw;
                        }
                    }

                    async Task StartPool(int attempt)
                    {
                        log($"Attempting (attempt: {attempt}) to start Application Pool: {pool.Name}");
                        pool.Start();
                        log($"Started Application Pool: {pool.Name}");
                        if (pool.State != ObjectState.Started)
                        {
                            await Task.Delay(500);
                            await StartPool(++attempt);
                        }
                    }

                    await performMove(1);
                    await StartPool(1);

                    await cli.PostAsync(new SlackMessage
                    {
                        Markdown = true,
                        Text = $":tada: {args[0]} version {version} deployed with great success! :tada:",
                    });
                }
                catch (Exception ex)
                {
                    log($"{ex}");
                    throw;
                }
            }));

            await Task.WhenAll(work);
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
