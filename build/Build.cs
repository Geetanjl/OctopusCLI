// ReSharper disable RedundantUsingDirective - prevent PrettyBot from getting confused about unused code.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using JetBrains.Annotations;
using Nuke.Common.CI;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Tools.OctoVersion;
using Nuke.Common.Tools.SignTool;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Readers.GZip;
using SharpCompress.Readers.Tar;
using SharpCompress.Writers;
using SharpCompress.Writers.Tar;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    const string CiBranchNameEnvVariable = "OCTOVERSION_CurrentBranch";

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")] readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;
    [Parameter("Pfx certificate to use for signing the files")] readonly AbsolutePath SigningCertificatePath = RootDirectory / "certificates" / "OctopusDevelopment.pfx";
    [Parameter("Password for the signing certificate")] readonly string SigningCertificatePassword = "Password01!";
    [Parameter("Branch name for OctoVersion to use to calculate the version number. Can be set via the environment variable " + CiBranchNameEnvVariable + ".", Name = CiBranchNameEnvVariable)]
    string BranchName { get; set; }
    [Parameter] readonly string RunNumber = "";

    [Solution(GenerateProjects = true)] readonly Solution Solution;

    [PackageExecutable(
        packageId: "OctoVersion.Tool",
        packageExecutable: "OctoVersion.Tool.dll",
        Framework = "net6.0")]
    readonly Tool OctoVersion;

    [PackageExecutable(
        packageId: "azuresigntool",
        packageExecutable: "azuresigntool.dll")]
    readonly Tool AzureSignTool = null!;

    [Parameter] readonly string AzureKeyVaultUrl = "";
    [Parameter] readonly string AzureKeyVaultAppId = "";
    [Parameter] [Secret] readonly string AzureKeyVaultAppSecret = "";
    [Parameter] readonly string AzureKeyVaultCertificateName = "";
    [Parameter] readonly string AzureKeyVaultTenantId = "";

    AbsolutePath SourceDirectory => RootDirectory / "source";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath PublishDirectory => RootDirectory / "publish";
    AbsolutePath AssetDirectory => RootDirectory / "BuildAssets";
    AbsolutePath LinuxPackageFeedsDir => RootDirectory / "linux-package-feeds";
    AbsolutePath OctopusCliDirectory => RootDirectory / "source" / "Octopus.Cli";
    AbsolutePath DotNetOctoCliFolder => RootDirectory / "source" / "Octopus.DotNet.Cli";
    AbsolutePath OctoPublishDirectory => PublishDirectory / "octo";
    AbsolutePath LocalPackagesDirectory => RootDirectory / ".." / "LocalPackages";

    string[] SigningTimestampUrls => new[]
    {
        "http://timestamp.comodoca.com/rfc3161",
        "http://timestamp.globalsign.com/tsa/r6advanced1", //https://support.globalsign.com/code-signing/code-signing-windows-7-8-and-10,
        "http://timestamp.digicert.com", //https://knowledge.digicert.com/solution/SO912.html
        "http://timestamp.apple.com/ts01", //https://gist.github.com/Manouchehri/fd754e402d98430243455713efada710
        "http://tsa.starfieldtech.com",
        "http://www.startssl.com/timestamp",
        "http://timestamp.verisign.com/scripts/timstamp.dll",
        "http://timestamp.globalsign.com/scripts/timestamp.dll",
        "https://rfc3161timestamp.globalsign.com/advanced"
    };

    string fullSemVer;

    Target Clean => _ => _
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj", "**/TestResults").ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
            EnsureCleanDirectory(PublishDirectory);
        });

    Target CalculateVersion => _ => _
        .Executes(() =>
        {
            var octoVersionText = RootDirectory / "octoversion.txt";

            Serilog.Log.Information("Looking for existing octoversion.txt in {Path}", octoVersionText);
            if (octoVersionText.FileExists())
            {
                Serilog.Log.Information("Found existing octoversion.txt in {Path}", octoVersionText);
                fullSemVer = File.ReadAllText(octoVersionText);

                Serilog.Log.Information("octoversion.txt has {FullSemVer}", fullSemVer);

                return;
            }

            // We are calculating the version to use explicitly here so we can support nightly builds with an incrementing number as well as only have non pre-releases for tagged commits
            var arguments = $"--CurrentBranch \"{BranchName ?? "local"}\" --NonPreReleaseTagsRegex \"refs/tags/[^-]*$\" --OutputFormats Json";

            var jObject = OctoVersion(arguments, customLogger: LogStdErrAsWarning).StdToJson();
            fullSemVer = jObject.Value<string>("FullSemVer");

            if (!IsLocalBuild && !string.IsNullOrEmpty(jObject.Value<string>("PreReleaseTag")))
            {
                // Without the dash would cause issues in Net6 for things like dependabot branches where the branch could end up looking like `9.0.0-SomeLib-1.1.0`.
                // In this case the version the script would come up with here would be `9.0.0-SomeLib-1.1.023` (if the run number was `23`).
                // SemVer does not like that leading `0` on `023`
                fullSemVer += $"-{RunNumber}";
            }

            File.WriteAllText(octoVersionText, fullSemVer);
            Console.WriteLine($"##[notice]Release version number: {fullSemVer}");
            Console.WriteLine($"::set-output name=version::{fullSemVer}");
        });

    Target Compile => _ => _
        .DependsOn(Clean)
        .DependsOn(CalculateVersion)
        .Executes(() =>
        {
            Serilog.Log.Information("Building OctopusCLI v{0}", fullSemVer);

            DotNetBuild(_ => _
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetVersion(fullSemVer));
        });

    [PublicAPI]
    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(_ => _
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .SetResultsDirectory(ArtifactsDirectory / "TestResults")
                .AddLoggers(
                    "console;verbosity=detailed",
                    "trx"
                ));
        });

    Target DotnetPublish => _ => _
        .DependsOn(Test)
        .Executes(() =>
        {
            var portablePublishDir = OctoPublishDirectory / "portable";
            DotNetPublish(_ => _
                .SetProject(Solution.Octo)
                .SetFramework("netcoreapp3.1")
                .SetConfiguration(Configuration)
                .SetOutput(portablePublishDir)
                .SetVersion(fullSemVer));

            SignBinaries(portablePublishDir);

            CopyFileToDirectory(AssetDirectory / "octo", portablePublishDir, FileExistsPolicy.Overwrite);
            CopyFileToDirectory(AssetDirectory / "octo.cmd", portablePublishDir, FileExistsPolicy.Overwrite);

            var doc = new XmlDocument();
            doc.Load(Solution.Octo.Path);
            var selectSingleNode = doc.SelectSingleNode("Project/PropertyGroup/RuntimeIdentifiers");
            if (selectSingleNode == null)
                throw new ApplicationException("Unable to find Project/PropertyGroup/RuntimeIdentifiers in Octo.csproj");
            var rids = selectSingleNode.InnerText;
            foreach (var rid in rids.Split(';'))
            {
                DotNetPublish(_ => _
                    .SetProject(Solution.Octo)
                    .SetConfiguration(Configuration)
                    .SetFramework("net6.0")
                    .SetRuntime(rid)
                    .EnableSelfContained()
                    .EnablePublishSingleFile()
                    .SetOutput(OctoPublishDirectory / rid)
                    .SetVersion(fullSemVer));

                if (!rid.StartsWith("linux-") && !rid.StartsWith("osx-"))
                    // Sign binaries, except linux which are verified at download, and osx which are signed on a mac
                    SignBinaries(OctoPublishDirectory / rid);
            }
        });


    Target Zip => _ => _
        .DependsOn(DotnetPublish)
        .Executes(() =>
        {
            foreach (var dir in Directory.EnumerateDirectories(OctoPublishDirectory))
            {
                var dirName = Path.GetFileName(dir);

                var outFile = ArtifactsDirectory / $"OctopusTools.{fullSemVer}.{dirName}";
                if (dirName == "portable" || dirName.Contains("win"))
                    CompressionTasks.CompressZip(dir, outFile + ".zip");

                if (!dirName.Contains("win"))
                    TarGzip(dir, outFile);
            }
        });

    Target PackOctopusToolsNuget => _ => _
        .DependsOn(DotnetPublish)
        .OnlyWhenStatic(() => EnvironmentInfo.IsWin)
        .Executes(() =>
        {
            var nugetPackDir = PublishDirectory / "nuget";
            var nuspecFile = "OctopusTools.nuspec";

            CopyDirectoryRecursively(OctoPublishDirectory / "win-x64", nugetPackDir);
            CopyFileToDirectory(AssetDirectory / "icon.png", nugetPackDir);
            CopyFileToDirectory(AssetDirectory / "LICENSE.txt", nugetPackDir);
            CopyFileToDirectory(AssetDirectory / "VERIFICATION.txt", nugetPackDir);
            CopyFileToDirectory(AssetDirectory / "init.ps1", nugetPackDir);
            CopyFileToDirectory(AssetDirectory / nuspecFile, nugetPackDir);

            NuGetTasks.NuGetPack(_ => _
                .SetTargetPath(nugetPackDir / nuspecFile)
                .SetVersion(fullSemVer)
                .SetOutputDirectory(ArtifactsDirectory));
        });

    Target PackDotNetOctoNuget => _ => _
        .DependsOn(DotnetPublish)
        .Executes(() =>
        {
            SignBinaries(OctopusCliDirectory / "bin" / Configuration);

            DotNetPack(_ => _
                .SetProject(OctopusCliDirectory)
                .SetConfiguration(Configuration)
                .SetOutputDirectory(ArtifactsDirectory)
                .SetVersion(fullSemVer)
                .EnableNoBuild()
                .DisableIncludeSymbols());

            SignBinaries(DotNetOctoCliFolder / "bin" / Configuration);

            DotNetPack(_ => _
                .SetProject(DotNetOctoCliFolder)
                .SetConfiguration(Configuration)
                .SetOutputDirectory(ArtifactsDirectory)
                .SetVersion(fullSemVer)
                .EnableNoBuild()
                .DisableIncludeSymbols());
        });

    Target Default => _ => _
        .DependsOn(PackOctopusToolsNuget)
        .DependsOn(PackDotNetOctoNuget)
        .DependsOn(Zip);

    void SignBinaries(string path)
    {
        if(IsLocalBuild) return;

        Serilog.Log.Information($"Signing binaries in {path}");

        var files = Directory.EnumerateFiles(path, "Octopus.*.dll", SearchOption.AllDirectories).ToList();
        files.AddRange(Directory.EnumerateFiles(path, "octo.dll", SearchOption.AllDirectories));
        files.AddRange(Directory.EnumerateFiles(path, "octo.exe", SearchOption.AllDirectories));
        files.AddRange(Directory.EnumerateFiles(path, "dotnet-octo.dll", SearchOption.AllDirectories));
        files.AddRange(Directory.EnumerateFiles(path, "octo*.dll", SearchOption.AllDirectories));
        files.AddRange(Directory.EnumerateFiles(path, "Octo*.dll", SearchOption.AllDirectories));
        var distinctFiles = files.Distinct().ToArray();

        var useSignTool = string.IsNullOrEmpty(AzureKeyVaultUrl)
            && string.IsNullOrEmpty(AzureKeyVaultAppId)
            && string.IsNullOrEmpty(AzureKeyVaultAppSecret)
            && string.IsNullOrEmpty(AzureKeyVaultCertificateName);

        var lastException = default(Exception);
        foreach (var url in SigningTimestampUrls)
        {
            Serilog.Log.Information("Signing and timestamping with server {Url}", url);
            try
            {
                if (useSignTool)
                    SignWithSignTool(distinctFiles, url);
                else
                    SignWithAzureSignTool(distinctFiles, url);
                lastException = null;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            if (lastException == null)
                break;
        }

        if (lastException != null)
            throw lastException;
        Serilog.Log.Information($"Finished signing {distinctFiles.Length} files.");
    }

    void SignWithAzureSignTool(IEnumerable<string> files, string timestampUrl)
    {
        Serilog.Log.Information("Signing files using azuresigntool and the production code signing certificate.");

        var arguments = "sign " +
            $"--azure-key-vault-url \"{AzureKeyVaultUrl}\" " +
            $"--azure-key-vault-client-id \"{AzureKeyVaultAppId}\" " +
            $"--azure-key-vault-tenant-id \"{AzureKeyVaultTenantId}\" " +
            $"--azure-key-vault-client-secret \"{AzureKeyVaultAppSecret}\" " +
            $"--azure-key-vault-certificate \"{AzureKeyVaultCertificateName}\" " +
            "--file-digest sha256 " +
            "--description \"Octopus CLI\" " +
            "--description-url \"https://octopus.com\" " +
            $"--timestamp-rfc3161 {timestampUrl} " +
            "--timestamp-digest sha256 ";

        foreach (var file in files)
            arguments += $"\"{file}\" ";

        AzureSignTool(arguments, customLogger: LogStdErrAsWarning);
    }

    void SignWithSignTool(IEnumerable<string> files, string url)
    {
        Serilog.Log.Information("Signing files using signtool.");
        SignToolTasks.SignToolLogger = LogStdErrAsWarning;

        SignToolTasks.SignTool(_ => _
            .SetFile(SigningCertificatePath)
            .SetPassword(SigningCertificatePassword)
            .SetFiles(files)
            .SetProcessToolPath(RootDirectory / "certificates" / "signtool.exe")
            .SetTimestampServerDigestAlgorithm("sha256")
            .SetDescription("Octopus CLI")
            .SetUrl("https://octopus.com")
            .SetRfc3161TimestampServerUrl(url));
    }

    static void LogStdErrAsWarning(OutputType type, string message)
    {
        if (type == OutputType.Err)
            Serilog.Log.Warning(message);
        else
            Serilog.Log.Debug(message);
    }

    void TarGzip(string path, string outputFile)
    {
        var outFile = $"{outputFile}.tar.gz";
        Serilog.Log.Information("Creating TGZ file {0} from {1}", outFile, path);
        using (var tarMemStream = new MemoryStream())
        {
            using (var tar = WriterFactory.Open(tarMemStream, ArchiveType.Tar, new TarWriterOptions(CompressionType.None, true)))
            {
                // Add the remaining files
                tar.WriteAll(path, "*", SearchOption.AllDirectories);
            }

            tarMemStream.Seek(0, SeekOrigin.Begin);

            using (Stream stream = File.Open(outFile, FileMode.Create))
            using (var zip = WriterFactory.Open(stream, ArchiveType.GZip, CompressionType.GZip))
            {
                zip.Write($"{outputFile}.tar", tarMemStream);
            }
        }

        Serilog.Log.Information("Successfully created TGZ file: {0}", outFile);
    }

    void UnTarGZip(string path, string destination)
    {
        using (var packageStream = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            using (var gzipReader = GZipReader.Open(packageStream))
            {
                gzipReader.MoveToNextEntry();
                using (var compressionStream = gzipReader.OpenEntryStream())
                {
                    using (var reader = TarReader.Open(compressionStream))
                    {
                        while (reader.MoveToNextEntry())
                        {
                            var entryDestination = Path.Combine(destination, reader.Entry.Key);
                            if (EnvironmentInfo.IsWin && File.Exists(entryDestination))
                                // In Windows, remove existing files before overwrite, to prevent existing filename case sticking
                                File.Delete(entryDestination);

                            reader.WriteEntryToDirectory(destination, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                        }
                    }
                }
            }
        }
    }

    /// Support plugins are available for:
    /// - JetBrains ReSharper        https://nuke.build/resharper
    /// - JetBrains Rider            https://nuke.build/rider
    /// - Microsoft VisualStudio     https://nuke.build/visualstudio
    /// - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Default);
}
