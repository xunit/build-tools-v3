namespace System;

internal static class StringExtensions
{
	public static string SafeReplace(
		this string arg,
		string? oldValue,
		string? newValue)
	{
		if (oldValue is null)
			return arg;

		return arg.Replace(oldValue, newValue);
	}
}
