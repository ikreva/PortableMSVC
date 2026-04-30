using System.Runtime.InteropServices;

namespace PortableMSVC;

internal static partial class CabNative
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint CabinetCallback(nint context, uint notification, nuint param1, nuint param2);

    [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupIterateCabinetW(string cabinetFile, uint reserved, CabinetCallback callback, nint context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FileInCabinetInfo
    {
        public nint NameInCabinet;
        public uint FileSize;
        public uint Win32Error;
        public ushort DosDate;
        public ushort DosTime;
        public ushort DosAttribs;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FullTargetName;
    }
}
