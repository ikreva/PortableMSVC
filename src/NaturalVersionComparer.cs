
namespace PortableMSVC;

public sealed class NaturalVersionComparer : IComparer<string>
{
	public static readonly NaturalVersionComparer Instance = new NaturalVersionComparer();

	public int Compare(string? x, string? y)
	{
		if (x == y)
		{
			return 0;
		}
		if (x == null)
		{
			return -1;
		}
		if (y == null)
		{
			return 1;
		}
		string[] left = Split(x);
		string[] right = Split(y);
		int count = Math.Max(left.Length, right.Length);
		for (int i = 0; i < count; i++)
		{
			string a = ((i < left.Length) ? left[i] : "0");
			string b = ((i < right.Length) ? right[i] : "0");
			int intA;
			bool numericA = int.TryParse(a, out intA);
			int intB;
			bool numericB = int.TryParse(b, out intB);
			int comparison = ((numericA && numericB) ? intA.CompareTo(intB) : StringComparer.OrdinalIgnoreCase.Compare(a, b));
			if (comparison != 0)
			{
				return comparison;
			}
		}
		return 0;
	}

	private static string[] Split(string value)
	{
		return value.Split(new char[3] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}
}
