using Xunit.BuildTools.Models;

namespace Xunit.BuildTools.Targets;

[Target(
	BuildTarget.SignAssemblies,
	BuildTarget.RestoreTools, BuildTarget.Build
)]
public static partial class SignAssemblies
{ }
