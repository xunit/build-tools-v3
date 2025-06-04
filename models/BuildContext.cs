using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bullseye;
using Bullseye.Internal;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Security.Extensions;
using SimpleExec;

namespace Xunit.BuildTools.Models;

[Command(Name = "build", Description = "Build utility for xUnit.net")]
[HelpOption("-?|-h|--help")]
public partial class BuildContext
{
	string? artifactsFolder;
	string? baseFolder;
	Version? dotNetSdkVersion;
	string? nuGetPackageCachePath;
	string? packageOutputFolder;
	string? signApplicationId = Environment.GetEnvironmentVariable("SIGN_APP_ID");
	string? signApplicationSecret = Environment.GetEnvironmentVariable("SIGN_APP_SECRET");
	string? signCertificateName = Environment.GetEnvironmentVariable("SIGN_CERT_NAME");
	string? signTenantId = Environment.GetEnvironmentVariable("SIGN_TENANT");
	string? signTimestampUri = Environment.GetEnvironmentVariable("SIGN_TIMESTAMP_URI");
	string? signVaultUri = Environment.GetEnvironmentVariable("SIGN_VAULT_URI");
	List<string>? skippedAnalysisFolders;
	List<string>? skippedAnalysisFoldersFull;
	List<Regex> skippedBomFilePatterns = new()
	{
		new Regex(@"\.user$"),
		new Regex(@"\.ncrunchsolution"),
		new Regex(@"\.ncrunchproject"),
	};
	HashSet<string> skippedBomFolderNames = new(StringComparer.OrdinalIgnoreCase)
	{
		".git",
		".idea",
		".vs",
		"artifacts",
		"bin",
		"obj",
	};
	string? testOutputFolder;

	// Calculated properties

	public string ArtifactsFolder
	{
		get => artifactsFolder ?? throw new InvalidOperationException($"Tried to retrieve unset {nameof(BuildContext)}.{nameof(ArtifactsFolder)}");
		private set => artifactsFolder = value ?? throw new ArgumentNullException(nameof(ArtifactsFolder));
	}

	public string BaseFolder
	{
		get => baseFolder ?? throw new InvalidOperationException($"Tried to retrieve unset {nameof(BuildContext)}.{nameof(BaseFolder)}");
		private set => baseFolder = value ?? throw new ArgumentNullException(nameof(BaseFolder));
	}

	public bool CanSign { get; private set; }

	public string ConfigurationText => Configuration.ToString();

	public Version DotNetSdkVersion
	{
		get => dotNetSdkVersion ?? throw new InvalidOperationException($"Tried to retrieve unset {nameof(BuildContext)}.{nameof(DotNetSdkVersion)}");
		private set => dotNetSdkVersion = value ?? throw new ArgumentNullException(nameof(DotNetSdkVersion));
	}

	public bool NeedMono { get; private set; }

	public string NuGetPackageCachePath
	{
		get => nuGetPackageCachePath ?? throw new InvalidOperationException($"Tried to retrieve unset {nameof(BuildContext)}.{nameof(NuGetPackageCachePath)}");
		private set => nuGetPackageCachePath = value ?? throw new ArgumentNullException(nameof(NuGetPackageCachePath));
	}

	public string PackageOutputFolder
	{
		get => packageOutputFolder ?? throw new InvalidOperationException($"Tried to retrieve unset {nameof(BuildContext)}.{nameof(PackageOutputFolder)}");
		private set => packageOutputFolder = value ?? throw new ArgumentNullException(nameof(PackageOutputFolder));
	}

	public IReadOnlyList<string> SkippedAnalysisFolders
	{
		get
		{
			skippedAnalysisFolders ??= GetSkippedAnalysisFolders().ToList();
			return skippedAnalysisFolders;
		}
	}

	public string TestOutputFolder
	{
		get => testOutputFolder ?? throw new InvalidOperationException($"Tried to retrieve unset {nameof(BuildContext)}.{nameof(TestOutputFolder)}");
		private set => testOutputFolder = value ?? throw new ArgumentNullException(nameof(TestOutputFolder));
	}

	internal BuildVerbosity VerbosityNuGet
	{
		get
		{
			return Verbosity switch
			{
				BuildVerbosity.diagnostic => BuildVerbosity.detailed,
				BuildVerbosity.minimal => BuildVerbosity.normal,
				_ => Verbosity,
			};
		}
	}

	// User-controllable command-line options

	[Option("-c|--configuration", Description = "Target configuration")]
	public Configuration Configuration { get; } = Configuration.Release;

	[Option("-N|--no-color", Description = "Disable colored output")]
	public bool NoColor { get; }

	[Option("-s|--skip-dependencies", Description = "Do not run targets' dependencies")]
	public bool SkipDependencies { get; }

	[Argument(0, "targets", Description = "The target(s) to run (default: 'BuildAll'; common values: 'Build', 'BuildAll', 'Clean', 'Restore', 'Test')")]
	public string[] Targets { get; } = new[] { BuildTarget.BuildAll };

	[Option("-t|--timing", Description = "Emit timing information for each target")]
	public bool Timing { get; }

	[Option("-v|--verbosity", Description = "Set verbosity level")]
	public BuildVerbosity Verbosity { get; } = BuildVerbosity.minimal;

	// Helper methods for build target consumption

	public void BuildStep(string message)
	{
		WriteLineColor(ConsoleColor.White, $"==> {message} <==");
		Console.WriteLine();
	}

	public async Task Exec(
		string name,
		string args,
		string? redactedArgs = null,
		string? workingDirectory = null)
	{
		redactedArgs ??= args;

		if (NeedMono && name.EndsWith(".exe"))
		{
			args = $"{name} {args}";
			redactedArgs = $"{name} {redactedArgs}";
			name = "mono";
		}

		var displayName =
			name.Contains(" ") || name.Contains("'")
				? "'" + name.Replace("'", "''") + "'"
				: name;

		WriteLineColor(ConsoleColor.DarkGray, $"EXEC: & {displayName} {redactedArgs}{Environment.NewLine}");

		await Command.RunAsync(name, args, workingDirectory ?? BaseFolder, /*noEcho*/ true);

		Console.WriteLine();
	}

	public IEnumerable<(string fileName, byte[] content)> FindFilesWithBOMs(string? folder = null)
	{
		skippedAnalysisFoldersFull ??= SkippedAnalysisFolders.Select(p => Path.GetFullPath(Path.Combine(BaseFolder, p))).ToList();
		folder ??= BaseFolder;

		if (skippedBomFolderNames.Contains(Path.GetFileName(folder)))
			yield break;

		if (skippedAnalysisFoldersFull.Any(f => f == folder))
			yield break;

		foreach (var file in Directory.GetFiles(folder))
		{
			if (skippedBomFilePatterns.Any(pattern => pattern.Match(file).Success))
				continue;

			byte[]? bytes = null;

			try
			{
				bytes = File.ReadAllBytes(file);
			}
			catch { }

			if (bytes != null && bytes.Length > 2 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
				yield return (file, bytes);
		}

		foreach (var directory in Directory.GetDirectories(folder))
			foreach (var result in FindFilesWithBOMs(directory))
				yield return result;
	}

	public partial IReadOnlyList<string> GetSkippedAnalysisFolders();

	partial void Initialize();

	async Task<int> OnExecuteAsync()
	{
		var swTotal = Stopwatch.StartNew();

		try
		{
			CanSign =
				!string.IsNullOrWhiteSpace(signApplicationId) &&
				!string.IsNullOrWhiteSpace(signCertificateName) &&
				!string.IsNullOrWhiteSpace(signTimestampUri) &&
				!string.IsNullOrWhiteSpace(signVaultUri);

			NeedMono = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

			// Find the NuGet package cache
			nuGetPackageCachePath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
			if (nuGetPackageCachePath is null)
			{
				var homeFolder = NeedMono ? Environment.GetEnvironmentVariable("HOME") : Environment.GetEnvironmentVariable("USERPROFILE");
				if (homeFolder != null)
					nuGetPackageCachePath = Path.Combine(homeFolder, ".nuget", "packages");
			}

			if (nuGetPackageCachePath is null)
			{
				WriteLineColor(ConsoleColor.Red, $"error: Could not find NuGet package cache (environment variable {(NeedMono ? "HOME" : "USERPROFILE")} is missing)");
				return -1;
			}

			nuGetPackageCachePath = Path.GetFullPath(nuGetPackageCachePath);
			if (!Directory.Exists(nuGetPackageCachePath))
			{
				WriteLineColor(ConsoleColor.Red, $"error: Expected to find NuGet package cache at {nuGetPackageCachePath} but it does not exist");
				return -1;
			}

			// Find the folder with the solution file
			var baseFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			while (true)
			{
				if (baseFolder == null)
					throw new InvalidOperationException("Could not locate a solution file in the directory hierarchy");

				if (Directory.GetFiles(baseFolder, "*.sln").Length != 0)
					break;

				baseFolder = Path.GetDirectoryName(baseFolder);
			}

			BaseFolder = baseFolder;

			// Dependent folders
			ArtifactsFolder = Path.Combine(BaseFolder, "artifacts");
			Directory.CreateDirectory(ArtifactsFolder);

			PackageOutputFolder = Path.Combine(ArtifactsFolder, "packages");
			Directory.CreateDirectory(PackageOutputFolder);

			TestOutputFolder = Path.Combine(ArtifactsFolder, "test");
			Directory.CreateDirectory(TestOutputFolder);

			// Get the version of the .NET SDK
			var dotnetProcessInfo = new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, RedirectStandardError = true };
			var dotnetProcess = Process.Start(dotnetProcessInfo);
			if (dotnetProcess is not null)
			{
				dotnetProcess.WaitForExit();

				if (dotnetProcess.ExitCode != 0)
					throw new InvalidOperationException("Could not execute 'dotnet --version'");

				var stdOutText = dotnetProcess.StandardOutput.ReadToEnd().Trim();
				DotNetSdkVersion = Version.Parse(stdOutText);
			}

			// Call dependent initialization, if there is one
			Initialize();

			// Parse the targets
			var targetNames = Targets.Select(x => x.ToString()).ToList();

			// Find target classes
			var targetCollection = new TargetCollection();
			var targets =
				Assembly
					.GetExecutingAssembly()
					.ExportedTypes
					.Select(x => new { type = x, attr = x.GetCustomAttribute<TargetAttribute>() });

			foreach (var target in targets)
				if (target.attr != null)
				{
					var method = target.type.GetRuntimeMethod("OnExecute", new[] { typeof(BuildContext) });

					if (method == null)
						targetCollection.Add(new Target(target.attr.TargetName, target.attr.DependentTargets));
					else
						targetCollection.Add(new ActionTarget(target.attr.TargetName, target.attr.DependentTargets, async () =>
						{
							var sw = Stopwatch.StartNew();

							try
							{
								var instance = method.IsStatic ? null : Activator.CreateInstance(target.type);
								var task = (Task?)method.Invoke(instance, new[] { this });
								if (task != null)
									await task;
							}
							finally
							{
								if (Timing)
									WriteLineColor(ConsoleColor.Cyan, $"TIMING: Target '{target.attr.TargetName}' took {sw.Elapsed}{Environment.NewLine}");
							}
						}));
				}

			// Let Bullseye run the target(s)
			await targetCollection.RunAsync(targetNames, SkipDependencies, dryRun: false, parallel: false, new NullLogger(), _ => false);

			WriteLineColor(ConsoleColor.Green, $"==> Build success! <=={Environment.NewLine}");

			return 0;
		}
		catch (Exception ex)
		{
			var error = ex;
			while ((error is TargetInvocationException || error is TargetFailedException) && error.InnerException != null)
				error = error.InnerException;

			Console.WriteLine();

			if (error is ExitCodeException nonZeroExit)
			{
				WriteLineColor(ConsoleColor.Red, "==> Build failed! <==");
				return nonZeroExit.ExitCode;
			}

			WriteLineColor(ConsoleColor.Red, $"==> Build failed! An unhandled exception was thrown <==");
			Console.WriteLine(error.ToString());
			return -1;
		}
		finally
		{
			if (Timing)
				WriteLineColor(ConsoleColor.Cyan, $"TIMING: Build took {swTotal.Elapsed}{Environment.NewLine}");
		}
	}

	public async Task SignFiles(
		string baseFolder,
		params string[] files)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			WriteLineColor(ConsoleColor.Red, "Code signing requires Microsoft Windows");
			throw new ExitCodeException(-1);
		}

		if (!CanSign)
		{
			WriteLineColor(ConsoleColor.Red, "One or more code signing environment variables are missing: SIGN_VAULT_URI, SIGN_TIMESTAMP_URI, SIGN_APP_ID, SIGN_CERT_NAME");
			throw new ExitCodeException(-1);
		}

		// Pass an empty file list, because NuGet packages will contain already-signed binaries
		var fileList = Path.GetTempFileName();

		try
		{
			foreach (var file in files)
			{
				var args =
					$"sign code azure-key-vault \"{file}\"" +
					$" --verbosity critical" +
					$" --base-directory \"{baseFolder}\"" +
					$" --description \"xUnit.net\"" +
					$" --description-url https://github.com/xunit" +
					$" --timestamp-url {signTimestampUri}" +
					$" --azure-key-vault-url {signVaultUri}" +
					$" --azure-key-vault-certificate {signCertificateName}";

				if (Path.GetExtension(file).Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
					args += $" --file-list \"{fileList}\"";

				string redactedArgs;

				if (signApplicationSecret is not null && signTenantId is not null)
				{
					args +=
						$" --azure-key-vault-client-id {signApplicationId}" +
						$" --azure-key-vault-client-secret \"{signApplicationSecret}\"" +
						$" --azure-key-vault-tenant-id {signTenantId}";
					redactedArgs =
						args
							.SafeReplace(signTenantId, "[redacted]")
							.SafeReplace(signVaultUri, "[redacted]")
							.SafeReplace(signApplicationId, "[redacted]")
							.SafeReplace(signApplicationSecret, "[redacted]")
							.SafeReplace(signCertificateName, "[redacted]");
				}
				else
				{
					args += $" --managed-identity-client-id {signApplicationId}";
					redactedArgs =
						args
							.SafeReplace(signVaultUri, "[redacted]")
							.SafeReplace(signApplicationId, "[redacted]")
							.SafeReplace(signCertificateName, "[redacted]");
				}

				await Exec("dotnet", args, redactedArgs);

				if (File.Exists(file))
				{
					using FileStream fs = File.OpenRead(file);
					var sigInfo = FileSignatureInfo.GetFromFileStream(fs);
					if (sigInfo.State != SignatureState.SignedAndTrusted)
						throw new InvalidOperationException($"Authenticode signature for {file} could not be verified");
				}
			}
		}
		finally
		{
			File.Delete(fileList);
		}
	}

	public void WriteColor(
		ConsoleColor foregroundColor,
		string text)
	{
		if (!NoColor)
			Console.ForegroundColor = foregroundColor;

		Console.Write(text);

		if (!NoColor)
			Console.ResetColor();
	}

	public void WriteLineColor(ConsoleColor foregroundColor, string text) =>
		WriteColor(foregroundColor, $"{text}{Environment.NewLine}");
}
