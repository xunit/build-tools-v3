using System.IO;
using System.Threading.Tasks;
using Xunit.BuildTools.Models;

namespace Xunit.BuildTools.Targets;

[Target(BuildTarget.Restore)]
public static partial class Restore
{
	public static Task OnExecute(BuildContext context)
	{
		context.BuildStep("Restoring NuGet packages");

		var buildLog = Path.Combine(context.BuildArtifactsFolder, "restore.binlog");

		return context.Exec("dotnet", $"msbuild -nologo -maxCpuCount -target:Restore -verbosity:{context.Verbosity} -binaryLogger:{buildLog}");
	}
}
