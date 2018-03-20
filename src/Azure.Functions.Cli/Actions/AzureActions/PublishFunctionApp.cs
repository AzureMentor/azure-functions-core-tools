﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Azure.Functions.Cli.Arm;
using Azure.Functions.Cli.Arm.Models;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Extensions;
using Azure.Functions.Cli.Helpers;
using Azure.Functions.Cli.Interfaces;
using Colors.Net;
using Fclp;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using static Azure.Functions.Cli.Common.OutputTheme;

namespace Azure.Functions.Cli.Actions.AzureActions
{
    [Action(Name = "publish", Context = Context.Azure, SubContext = Context.FunctionApp, HelpText = "Publish the current directory contents to an Azure Function App. Locally deleted files are not removed from destination.")]
    internal class PublishFunctionApp : BaseFunctionAppAction
    {
        private readonly ISettings _settings;
        private readonly ISecretsManager _secretsManager;
        private readonly IArmTokenManager _tokenManager;

        public bool PublishLocalSettings { get; set; }
        public bool OverwriteSettings { get; set; }
        public bool PublishLocalSettingsOnly { get; set; }
        public bool ListIgnoredFiles { get; set; }
        public bool ListIncludedFiles { get; set; }

        public PublishFunctionApp(IArmManager armManager, ISettings settings, ISecretsManager secretsManager, IArmTokenManager tokenManager)
            : base(armManager)
        {
            _settings = settings;
            _secretsManager = secretsManager;
            _tokenManager = tokenManager;
        }

        public override ICommandLineParserResult ParseArgs(string[] args)
        {
            Parser
                .Setup<bool>('i', "publish-local-settings")
                .WithDescription("Updates App Settings for the function app in Azure during deployment.")
                .Callback(f => PublishLocalSettings = f);
            Parser
                .Setup<bool>('o', "publish-settings-only")
                .WithDescription("Only publish settings and skip the content. Default is prompt.")
                .Callback(f => PublishLocalSettingsOnly = f);
            Parser
                .Setup<bool>('y', "overwrite-settings")
                .WithDescription("Only to be used in conjunction with -i or -o. Overwrites AppSettings in Azure with local value if different. Default is prompt.")
                .Callback(f => OverwriteSettings = f);
            Parser
                .Setup<bool>("list-ignored-files")
                .WithDescription("Displays a list of files that will be ignored from publishing based on .funcignore")
                .Callback(f => ListIgnoredFiles = f);
            Parser
                .Setup<bool>("list-included-files")
                .WithDescription("Displays a list of files that will be included in publishing based on .funcignore")
                .Callback(f => ListIncludedFiles = f);

            return base.ParseArgs(args);
        }

        public override async Task RunAsync()
        {
            GitIgnoreParser ignoreParser = null;
            try
            {
                var path = Path.Combine(Environment.CurrentDirectory, Constants.FuncIgnoreFile);
                if (FileSystemHelpers.FileExists(path))
                {
                    ignoreParser = new GitIgnoreParser(FileSystemHelpers.ReadAllTextFromFile(path));
                }
            }
            catch { }

            if (ListIncludedFiles)
            {
                InternalListIncludedFiles(ignoreParser);
            }
            else if (ListIgnoredFiles)
            {
                InternalListIgnoredFiles(ignoreParser);
            }
            else
            {
                if (PublishLocalSettingsOnly)
                {
                    await InternalPublishLocalSettingsOnly();
                }
                else
                {
                    await InternalPublishFunctionApp(ignoreParser);
                }
            }
        }

        private async Task InternalPublishFunctionApp(GitIgnoreParser ignoreParser)
        {
            var functionApp = await _armManager.GetFunctionAppAsync(FunctionAppName);
            var functionAppRoot = ScriptHostHelpers.GetFunctionAppRootDirectory(Environment.CurrentDirectory);
            // ColoredConsole.WriteLine(WarningColor($"Publish {functionAppRoot} contents to an Azure Function App. Locally deleted files are not removed from destination."));

            // create zip
            // get app settings
            // upload to storage
            // get sas
            // update app settings

            ColoredConsole.WriteLine("Preparing archive...");
            var zip = CreateZip(functionAppRoot, ignoreParser);
            var azureAppSettings = await _armManager.GetFunctionAppAppSettings(functionApp);

            var sas = await UploadZipToStorage(zip, azureAppSettings);

            azureAppSettings["WEBSITE_USE_ZIP"] = sas;

            var result = await _armManager.UpdateFunctionAppAppSettings(functionApp, azureAppSettings);
            if (!result.IsSuccessful)
            {
                ColoredConsole
                    .Error
                    .WriteLine(ErrorColor("Error updating app settings:"))
                    .WriteLine(ErrorColor(result.ErrorResult));
            }
            else
            {
                ColoredConsole.WriteLine("Uploading content...");
                await Task.Delay(2000);
                ColoredConsole.WriteLine("Upload completed successfully.");
                await Task.Delay(2000);
                ColoredConsole.WriteLine("Deployment completed successfully.");
            }
        }

        private async Task<string> UploadZipToStorage(Stream zip, Dictionary<string, string> appSettings)
        {
            const string containerName = "function-releases";
            const string blobNameFormat = "{0}-{1}.zip";
            var storageConnection = appSettings["AzureWebJobsStorage"];
            var storageAccount = CloudStorageAccount.Parse(storageConnection);
            var blobClient = storageAccount.CreateCloudBlobClient();
            var blobContainer = blobClient.GetContainerReference(containerName);
            await blobContainer.CreateIfNotExistsAsync();

            var releaseName = Guid.NewGuid().ToString();
            var blob = blobContainer.GetBlockBlobReference(string.Format(blobNameFormat, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), releaseName));
            await blob.UploadFromStreamAsync(zip);

            var sasConstraints = new SharedAccessBlobPolicy();
            sasConstraints.SharedAccessStartTime = DateTimeOffset.UtcNow.AddMinutes(-5);
            sasConstraints.SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddYears(20);
            sasConstraints.Permissions = SharedAccessBlobPermissions.Read;

            var blobToken = blob.GetSharedAccessSignature(sasConstraints);

            return blob.Uri + blobToken;
        }

        private static IEnumerable<string> GetFiles(string path)
        {
            return FileSystemHelpers.GetFiles(path, new[] { ".git", ".vscode" }, new[] { ".funcignore", ".gitignore", "appsettings.json", "local.settings.json", "project.lock.json" });
        }

        private async Task InternalPublishLocalSettingsOnly()
        {
            ColoredConsole.WriteLine("Getting site publishing info...");
            var functionApp = await _armManager.GetFunctionAppAsync(FunctionAppName);
            var isSuccessful = await PublishAppSettings(functionApp);
            if (!isSuccessful)
            {
                return;
            }
        }

        private static void InternalListIgnoredFiles(GitIgnoreParser ignoreParser)
        {
            if (ignoreParser == null)
            {
                ColoredConsole.Error.WriteLine("No .funcignore file");
                return;
            }

            foreach (var file in GetFiles(Environment.CurrentDirectory).Select(f => f.Replace(Environment.CurrentDirectory, "").Trim(Path.DirectorySeparatorChar).Replace("\\", "/")))
            {
                if (ignoreParser.Denies(file))
                {
                    ColoredConsole.WriteLine(file);
                }
            }
        }

        private static void InternalListIncludedFiles(GitIgnoreParser ignoreParser)
        {
            if (ignoreParser == null)
            {
                ColoredConsole.Error.WriteLine("No .funcignore file");
                return;
            }

            foreach (var file in GetFiles(Environment.CurrentDirectory).Select(f => f.Replace(Environment.CurrentDirectory, "").Trim(Path.DirectorySeparatorChar).Replace("\\", "/")))
            {
                if (ignoreParser.Accepts(file))
                {
                    ColoredConsole.WriteLine(file);
                }
            }
        }

        private async Task<bool> PublishAppSettings(Site functionApp)
        {
            var azureAppSettings = await _armManager.GetFunctionAppAppSettings(functionApp);
            var localAppSettings = _secretsManager.GetSecrets();
            var appSettings = MergeAppSettings(azureAppSettings, localAppSettings);
            var result = await _armManager.UpdateFunctionAppAppSettings(functionApp, appSettings);
            if (!result.IsSuccessful)
            {
                ColoredConsole
                    .Error
                    .WriteLine(ErrorColor("Error updating app settings:"))
                    .WriteLine(ErrorColor(result.ErrorResult));
                return false;
            }
            return true;
        }

        private IDictionary<string, string> MergeAppSettings(IDictionary<string, string> azure, IDictionary<string, string> local)
        {
            var result = new Dictionary<string, string>(azure);
            foreach (var pair in local)
            {
                if (result.ContainsKeyCaseInsensitive(pair.Key) &&
                    !result.GetValueCaseInsensitive(pair.Key).Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    ColoredConsole.WriteLine($"App setting {pair.Key} is different between azure and {SecretsManager.AppSettingsFileName}");
                    if (OverwriteSettings)
                    {
                        ColoredConsole.WriteLine("Overwriting setting in azure with local value because '--overwrite-settings [-y]' was specified.");
                        result[pair.Key] = pair.Value;
                    }
                    else
                    {
                        var answer = string.Empty;
                        do
                        {
                            ColoredConsole.WriteLine(QuestionColor("Would you like to overwrite value in azure? [yes/no/show]"));
                            answer = Console.ReadLine();
                            if (answer.Equals("show", StringComparison.OrdinalIgnoreCase))
                            {
                                ColoredConsole
                                    .WriteLine($"Azure: {azure.GetValueCaseInsensitive(pair.Key)}")
                                    .WriteLine($"Locally: {pair.Value}");
                            }
                        } while (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase) &&
                                 !answer.Equals("no", StringComparison.OrdinalIgnoreCase));

                        if (answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
                        {
                            result[pair.Key] = pair.Value;
                        }
                    }
                }
                else
                {
                    ColoredConsole.WriteLine($"Setting {pair.Key} = ****");
                    result[pair.Key] = pair.Value;
                }
            }

            return result;
        }

        private static MemoryStream CreateZip(string path, GitIgnoreParser ignoreParser)
        {
            var memoryStream = new MemoryStream();
            using (var zip = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var fileName in GetFiles(path))
                {
                    if (ignoreParser?.Accepts(fileName.Replace(path, "").Trim(Path.DirectorySeparatorChar).Replace("\\", "/")) ?? true)
                    {
                        zip.AddFile(fileName, fileName, path);
                    }
                }
            }
            memoryStream.Seek(0, SeekOrigin.Begin);
            return memoryStream;
        }

        private async Task<HttpClient> GetRemoteZipClient(Uri url)
        {
            var client = new HttpClient
            {
                BaseAddress = url,
                MaxResponseContentBufferSize = 30 * 1024 * 1024,
                Timeout = Timeout.InfiniteTimeSpan
            };
            var token = await _tokenManager.GetToken(_settings.CurrentTenant);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
    }
}
