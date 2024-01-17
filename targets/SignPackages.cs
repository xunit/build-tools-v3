using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit.BuildTools.Models;

namespace Xunit.BuildTools.Targets;

[Target(
	BuildTarget.SignPackages,
	BuildTarget.RestoreTools
)]
public static partial class SignPackages
{
	public static async Task OnExecute(BuildContext context)
	{
		context.BuildStep("Signing NuGet packages");

		var tenantId = Environment.GetEnvironmentVariable("SIGN_TENANT");
		var vaultUri = Environment.GetEnvironmentVariable("SIGN_VAULT_URI");
		var signTimestampUri = Environment.GetEnvironmentVariable("SIGN_TIMESTAMP_URI");
		var applicationId = Environment.GetEnvironmentVariable("SIGN_APP_ID");
		var applicationSecret = Environment.GetEnvironmentVariable("SIGN_APP_SECRET");
		var certificateName = Environment.GetEnvironmentVariable("SIGN_CERT_NAME");

		if (string.IsNullOrWhiteSpace(tenantId) ||
			string.IsNullOrWhiteSpace(vaultUri) ||
			string.IsNullOrWhiteSpace(signTimestampUri) ||
			string.IsNullOrWhiteSpace(applicationId) ||
			string.IsNullOrWhiteSpace(applicationSecret) ||
			string.IsNullOrWhiteSpace(certificateName))
		{
			context.WriteLineColor(ConsoleColor.Yellow, $"Skipping packing signing because one or more environment variables are missing: SIGN_TENANT, SIGN_VAULT_URI, SIGN_TIMESTAMP_URI, SIGN_APP_ID, SIGN_APP_SECRET, SIGN_CERT_NAME{Environment.NewLine}");
			return;
		}

		foreach (var nupkgFile in Directory.GetFiles(context.PackageOutputFolder, "*.nupkg"))
		{
			var args =
				$"sign code azure-key-vault \"{nupkgFile}\"" +
				$" --description \"xUnit.net\"" +
				$" --description-url https://github.com/xunit" +
				$" --timestamp-url {signTimestampUri}" +
				$" --azure-key-vault-url {vaultUri}" +
				$" --azure-key-vault-client-id {applicationId}" +
				$" --azure-key-vault-client-secret \"{applicationSecret}\"" +
				$" --azure-key-vault-tenant-id {tenantId}" +
				$" --azure-key-vault-certificate {certificateName}";

			// Append the file list, if it's present. We need to remove both the extension and the last part of the filename
			// because NuGet packages are formatted as (name).(version).nupkg. SemVer regular expression adapted from
			// https://semver.org/#is-there-a-suggested-regular-expression-regex-to-check-a-semver-string
			var semVerRegex = new Regex("^(.*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-((?:0|[1-9]\\d*|\\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\\.(?:0|[1-9]\\d*|\\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\\+([0-9a-zA-Z-]+(?:\\.[0-9a-zA-Z-]+)*))?$");
			var match = semVerRegex.Match(Path.GetFileNameWithoutExtension(nupkgFile));
			if (match.Success)
			{
				var fileList = Path.Combine(context.PackageOutputFolder, match.Groups[1].Value + ".sign-file-list");
				if (File.Exists(fileList))
					args += $" --file-list \"{fileList}\"";
			}

			var redactedArgs =
				args.Replace(tenantId, "[redacted]")
					.Replace(vaultUri, "[redacted]")
					.Replace(applicationId, "[redacted]")
					.Replace(applicationSecret, "[redacted]")
					.Replace(certificateName, "[redacted]");

			await context.Exec("dotnet", args, redactedArgs);
		}
	}
}
