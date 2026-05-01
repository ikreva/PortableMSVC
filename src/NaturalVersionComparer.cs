
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

		ReadOnlySpan<char> left = x.AsSpan();
		ReadOnlySpan<char> right = y.AsSpan();
		int leftIndex = 0;
		int rightIndex = 0;
		while (true)
		{
			bool hasLeft = TryReadSegment(left, ref leftIndex, out ReadOnlySpan<char> leftSegment);
			bool hasRight = TryReadSegment(right, ref rightIndex, out ReadOnlySpan<char> rightSegment);
			if (!hasLeft && !hasRight)
			{
				return 0;
			}

			int comparison = CompareSegment(hasLeft ? leftSegment : "0", hasRight ? rightSegment : "0");
			if (comparison != 0)
			{
				return comparison;
			}
		}
	}

	private static int CompareSegment(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
	{
		bool numericLeft = int.TryParse(left, out int leftValue);
		bool numericRight = int.TryParse(right, out int rightValue);
		return numericLeft && numericRight
			? leftValue.CompareTo(rightValue)
			: left.CompareTo(right, StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryReadSegment(ReadOnlySpan<char> value, ref int index, out ReadOnlySpan<char> segment)
	{
		while (index < value.Length)
		{
			while (index < value.Length && IsSeparator(value[index]))
			{
				index++;
			}

			int start = index;
			while (index < value.Length && !IsSeparator(value[index]))
			{
				index++;
			}

			segment = value[start..index].Trim();
			if (segment.Length > 0)
			{
				return true;
			}
		}

		segment = default;
		return false;
	}

	private static bool IsSeparator(char value)
	{
		return value is '.' or '-' or '_';
	}
}
