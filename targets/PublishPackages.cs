using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit.BuildTools.Models;

namespace Xunit.BuildTools.Targets;

[Target(
	BuildTarget.PublishPackages,
	BuildTarget.SignPackages
)]
public static partial class PublishPackages
{
	public static async Task OnExecute(BuildContext context)
	{
		context.BuildStep("Publishing NuGet packages");

		var pushApiKey = Environment.GetEnvironmentVariable("PUSH_APIKEY");
		var pushUri = Environment.GetEnvironmentVariable("PUSH_URI");

		if (string.IsNullOrWhiteSpace(pushApiKey) || string.IsNullOrWhiteSpace(pushUri))
		{
			context.WriteLineColor(ConsoleColor.Yellow, $"Skipping package publishing because environment variable 'PUSH_APIKEY' or 'PUSH_URI' is not set.{Environment.NewLine}");
			return;
		}

		var packageFiles =
			Directory
				.GetFiles(context.PackageOutputFolder, "*.nupkg", SearchOption.AllDirectories)
				.Select(x => x.Substring(context.BaseFolder.Length + 1));

		foreach (var packageFile in packageFiles.OrderBy(x => x))
		{
			var args = $"nuget push --source {pushUri} --api-key {pushApiKey} {packageFile}";
			var redactedArgs = args.Replace(pushApiKey, "[redacted]");
			await context.Exec("dotnet", args, redactedArgs);
		}
	}
}
