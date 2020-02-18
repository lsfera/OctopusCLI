//////////////////////////////////////////////////////////////////////
// TOOLS
//////////////////////////////////////////////////////////////////////
#tool "nuget:?package=GitVersion.CommandLine&version=4.0.0"
#tool "nuget:?package=ILRepack&version=2.0.13"
#addin "nuget:?package=SharpCompress&version=0.12.4"
#addin "nuget:?package=Cake.Incubator&version=5.1.0"

using SharpCompress;
using SharpCompress.Common;
using SharpCompress.Writer;
using System.Xml;
using Cake.Incubator;
using Cake.Incubator.LoggingExtensions;

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////
var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var signingCertificatePath = Argument("signing_certificate_path", "");
var signingCertificatePassword = Argument("signing_certificate_password", "");

///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////
var publishDir = "./publish";
var artifactsDir = "./artifacts";
var assetDir = "./BuildAssets";
var localPackagesDir = "../LocalPackages";
var globalAssemblyFile = "./source/Octo/Properties/AssemblyInfo.cs";
var projectToPublish = "./source/Octo/Octo.csproj";
var octoPublishFolder = $"{publishDir}/Octo";
var octoMergedFolder =  $"{publishDir}/OctoMerged";
var octopusCliFolder = "./source/Octopus.Cli";
var dotNetOctoCliFolder = "./source/Octopus.DotNet.Cli";
var dotNetOctoPublishFolder = $"{publishDir}/dotnetocto";
var dotNetOctoMergedFolder =  $"{publishDir}/dotnetocto-Merged";

GitVersion gitVersionInfo;
string nugetVersion;


///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////
Setup(context =>
{
     gitVersionInfo = GitVersion(new GitVersionSettings {
        OutputType = GitVersionOutput.Json
    });
    nugetVersion = gitVersionInfo.NuGetVersion;

    if(BuildSystem.IsRunningOnTeamCity)
        BuildSystem.TeamCity.SetBuildNumber(nugetVersion);

    Information("Building OctopusCli v{0}", nugetVersion);
    Information("Informational Version {0}", gitVersionInfo.InformationalVersion);
    Verbose("GitVersion:\n{0}", gitVersionInfo.Dump());
});

Teardown(context =>
{
    Information("Finished running tasks for build v{0}", nugetVersion);
});

//////////////////////////////////////////////////////////////////////
//  PRIVATE TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
{
    CleanDirectory(artifactsDir);
    CleanDirectory(publishDir);
    CleanDirectories("./source/**/bin");
    CleanDirectories("./source/**/obj");
    CleanDirectories("./source/**/TestResults");
});

Task("Restore")
    .IsDependentOn("Clean")
    .Does(() => DotNetCoreRestore("source", new DotNetCoreRestoreSettings
        {
            ArgumentCustomization = args => args.Append($"/p:Version={nugetVersion}")
        }));

Task("Build")
    .IsDependentOn("Restore")
    .IsDependentOn("Clean")
    .Does(() =>
{
    DotNetCoreBuild("./source", new DotNetCoreBuildSettings
    {
        Configuration = configuration,
        ArgumentCustomization = args => args.Append($"/p:Version={nugetVersion}")
    });
});

Task("Test")
    .IsDependentOn("Build")
    .Does(() =>
    {
        GetFiles("**/**/*Tests.csproj")
            .ToList()
            .ForEach(testProjectFile =>
            {
                DotNetCoreTest(testProjectFile.FullPath, new DotNetCoreTestSettings
                {
                    Configuration = configuration,
                    NoBuild = true
                });
            });
    });

Task("DotnetPublish")
    .IsDependentOn("Test")
    .Does(() =>
{
    DotNetCorePublish(projectToPublish, new DotNetCorePublishSettings
    {
        Framework = "net451",
        Configuration = configuration,
        OutputDirectory = $"{octoPublishFolder}/netfx",
        ArgumentCustomization = args => args.Append($"/p:Version={nugetVersion}")
    });

    var portablePublishDir =  $"{octoPublishFolder}/portable";
    DotNetCorePublish(projectToPublish, new DotNetCorePublishSettings
    {
        Framework = "netcoreapp2.0" /* For compatibility until we gently phase it out. We encourage upgrading to self-contained executable. */,
        Configuration = configuration,
        OutputDirectory = portablePublishDir,
        ArgumentCustomization = args => args.Append($"/p:Version={nugetVersion}")
    });
    SignBinaries(portablePublishDir);

    CopyFileToDirectory($"{assetDir}/octo", portablePublishDir);
    CopyFileToDirectory($"{assetDir}/octo.cmd", portablePublishDir);

    var doc = new XmlDocument();
    doc.Load(@".\source\Octo\Octo.csproj");
    var rids = doc.SelectSingleNode("Project/PropertyGroup/RuntimeIdentifiers").InnerText;
    foreach (var rid in rids.Split(';'))
    {
        DotNetCorePublish(projectToPublish, new DotNetCorePublishSettings
        {
            Framework = "netcoreapp3.1",
            Configuration = configuration,
            Runtime = rid,
            OutputDirectory = $"{octoPublishFolder}/{rid}",
			ArgumentCustomization = args => args.Append($"/p:Version={nugetVersion}"),
            SelfContained = true,
            PublishSingleFile = true
        });
        if (!rid.StartsWith("linux-") && !rid.StartsWith("osx-")) {
            // Sign binaries, except linux which are verified at download, and osx which are signed on a mac
            SignBinaries($"{octoPublishFolder}/{rid}");
        }
    }
});

Task("MergeOctoExe")
    .IsDependentOn("DotnetPublish")
    .Does(() => {
        var inputFolder = $"{octoPublishFolder}/netfx";
        var outputFolder = $"{octoPublishFolder}/netfx-merged";
        CreateDirectory(outputFolder);
        ILRepack(
            $"{outputFolder}/octo.exe",
            $"{inputFolder}/octo.exe",
            System.IO.Directory.EnumerateFiles(inputFolder, "*.dll")
				.Union(System.IO.Directory.EnumerateFiles(inputFolder, "octodiff.exe"))
				.Select(f => (FilePath) f),
            new ILRepackSettings {
                Internalize = true,
                Parallel = true,
                Libs = new List<DirectoryPath>() { inputFolder }
            }
        );
        SignBinaries(outputFolder);
    });


Task("Zip")
    .IsDependentOn("MergeOctoExe")
    .IsDependentOn("DotnetPublish")
    .Does(() => {


        foreach(var dir in System.IO.Directory.EnumerateDirectories(octoPublishFolder))
        {
            var dirName = System.IO.Path.GetFileName(dir);

            if(dirName == "netfx")
                continue;

            if(dirName == "netfx-merged")
            {
                Zip(dir, $"{artifactsDir}/OctopusTools.{nugetVersion}.zip");
            }
            else
            {
                var outFile = $"{artifactsDir}/OctopusTools.{nugetVersion}.{dirName}";
                if(dirName == "portable" || dirName.Contains("win"))
                    Zip(dir, outFile + ".zip");

                if(!dirName.Contains("win"))
                    TarGzip(dir, outFile,
                        insertUpperCaseOctoWrapper: dirName.Contains("linux"),
                        insertUpperCaseDotNetWrapper: dirName == "portable");
            }
        }
    });


Task("PackOctopusToolsNuget")
    .IsDependentOn("DotnetPublish")
    .Does(() => {
        var nugetPackDir = $"{publishDir}/nuget";
        var nuspecFile = "OctopusTools.nuspec";

        CopyDirectory($"{octoPublishFolder}/win-x64", nugetPackDir);
        CopyFileToDirectory($"{assetDir}/LICENSE.txt", nugetPackDir);
        CopyFileToDirectory($"{assetDir}/VERIFICATION.txt", nugetPackDir);
        CopyFileToDirectory($"{assetDir}/init.ps1", nugetPackDir);
        CopyFileToDirectory($"{assetDir}/{nuspecFile}", nugetPackDir);

        NuGetPack($"{nugetPackDir}/{nuspecFile}", new NuGetPackSettings {
            Version = nugetVersion,
            OutputDirectory = artifactsDir
        });
    });

Task("PackDotNetOctoNuget")
	.IsDependentOn("DotnetPublish")
    .Does(() => {

		SignBinaries($"{octopusCliFolder}/bin/{configuration}");

		DotNetCorePack(octopusCliFolder, new DotNetCorePackSettings
		{
			Configuration = configuration,
			OutputDirectory = artifactsDir,
			ArgumentCustomization = args => args.Append($"/p:Version={nugetVersion}"),
            NoBuild = true,
            IncludeSymbols = false
		});

		SignBinaries($"{dotNetOctoCliFolder}/bin/{configuration}");

		DotNetCorePack(dotNetOctoCliFolder, new DotNetCorePackSettings
		{
			Configuration = configuration,
			OutputDirectory = artifactsDir,
			ArgumentCustomization = args => args.Append($"/p:Version={nugetVersion}"),
            NoBuild = true,
            IncludeSymbols = false
		});
    });

Task("CopyToLocalPackages")
    .WithCriteria(BuildSystem.IsLocalBuild)
    .IsDependentOn("PackOctopusToolsNuget")
    .IsDependentOn("PackDotNetOctoNuget")
    .IsDependentOn("Zip")
    .Does(() =>
{
    CreateDirectory(localPackagesDir);
    CopyFileToDirectory($"{artifactsDir}/Octopus.Cli.{nugetVersion}.nupkg", localPackagesDir);
    CopyFileToDirectory($"{artifactsDir}/Octopus.DotNet.Cli.{nugetVersion}.nupkg", localPackagesDir);
});

private void SignBinaries(string path)
{
    Information($"Signing binaries in {path}");
	var files = GetFiles(path + "/**/Octopus.*.dll");
    files.Add(GetFiles(path + "/**/octo.dll"));
    files.Add(GetFiles(path + "/**/octo.exe"));
    files.Add(GetFiles(path + "/**/octo"));
    files.Add(GetFiles(path + "/**/dotnet-octo.dll"));

	Sign(files, new SignToolSignSettings {
			ToolPath = MakeAbsolute(File("./certificates/signtool.exe")),
            TimeStampUri = new Uri("http://timestamp.globalsign.com/scripts/timestamp.dll"),
            CertPath = signingCertificatePath,
            Password = signingCertificatePassword
    });
}


private void TarGzip(string path, string outputFile, bool insertUpperCaseOctoWrapper = false, bool insertUpperCaseDotNetWrapper = false)
{
    var outFile = $"{outputFile}.tar.gz";
    Information("Creating TGZ file {0} from {1}", outFile, path);
    using (var tarMemStream = new MemoryStream())
    {
        using (var tar = WriterFactory.Open(tarMemStream, ArchiveType.Tar, CompressionType.None, true))
        {
            // If using a capitalized wrapper, insert it first so it wouldn't overwrite the main payload on a case-insensitive system.
            if (insertUpperCaseOctoWrapper) {
                tar.Write("Octo", $"{assetDir}/OctoWrapper.sh");
            } else if (insertUpperCaseDotNetWrapper) {
                tar.Write("Octo", $"{assetDir}/octo");
            }

            // Add the remaining files
            tar.WriteAll(path, "*", SearchOption.AllDirectories);
        }

        tarMemStream.Seek(0, SeekOrigin.Begin);

        using (Stream stream = System.IO.File.Open(outFile, FileMode.Create))
        using (var zip = WriterFactory.Open(stream, ArchiveType.GZip, CompressionType.GZip))
            zip.Write($"{outputFile}.tar", tarMemStream);
    }
    Information("Successfully created TGZ file: {0}", outFile);
}



//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////
Task("Default")
    .IsDependentOn("CopyToLocalPackages");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////
RunTarget(target);