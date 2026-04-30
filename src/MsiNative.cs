using System.Runtime.InteropServices;
using System.Text;

namespace PortableMSVC;

internal static partial class MsiNative
{
    internal const int ErrorSuccess = 0;

    [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MsiOpenDatabaseW(string databasePath, nint persist, out nint database);

    [LibraryImport("msi.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MsiDatabaseOpenViewW(nint database, string query, out nint view);

    [LibraryImport("msi.dll")]
    internal static partial int MsiViewExecute(nint view, nint record);

    [LibraryImport("msi.dll")]
    internal static partial int MsiViewFetch(nint view, out nint record);

    [LibraryImport("msi.dll")]
    internal static partial int MsiCloseHandle(nint handle);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    internal static extern int MsiRecordGetStringW(nint record, uint field, StringBuilder? value, ref uint valueLength);

    [LibraryImport("msi.dll")]
    internal static partial int MsiRecordGetInteger(nint record, uint field);

    [LibraryImport("msi.dll")]
    internal static partial uint MsiRecordGetFieldCount(nint record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    internal static extern int MsiRecordReadStream(nint record, uint field, byte[] buffer, ref uint bufferSize);

    internal static string GetString(nint record, uint field)
    {
        uint length = 0;
        _ = MsiRecordGetStringW(record, field, null, ref length);
        length++;
        var builder = new StringBuilder((int)length);
        var result = MsiRecordGetStringW(record, field, builder, ref length);
        if (result != ErrorSuccess)
        {
            return "";
        }

        return builder.ToString();
    }
}
