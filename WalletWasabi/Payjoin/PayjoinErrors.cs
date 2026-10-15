using System.Reflection;

namespace WalletWasabi.Payjoin;

internal static class PayjoinErrors
{
	/// <summary>
	/// The library's error enums nest their payload in a field called <c>v1</c> and inherit the
	/// placeholder <see cref="Exception.Message"/>; the innermost error prints its own text.
	/// </summary>
	public static string Describe(Exception ex) => ex is global::Payjoin.UniffiException ? DescribeLibraryError(ex) : ex.Message;

	private static string DescribeLibraryError(object error)
	{
		if (error.GetType().GetField("v1", BindingFlags.Public | BindingFlags.Instance) is { } payload && payload.GetValue(error) is { } inner)
		{
			return inner as string ?? DescribeLibraryError(inner);
		}

		string description = error.ToString() ?? "";
		return description.StartsWith(error.GetType().FullName ?? "", StringComparison.Ordinal) ? error.GetType().Name : description;
	}
}
