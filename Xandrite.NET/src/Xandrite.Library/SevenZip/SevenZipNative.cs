using System.Runtime.InteropServices;
using System.Text;

namespace Xandrite.Library.SevenZip;

/*
 * 7-Zip's archive library (7z.dll on Windows), called in-process.
 *
 * 7z.dll exports CreateObject, which makes an archive handler (IInArchive) for a format. The handler reads through
 * streams and callbacks that the caller provides (see SevenZipCallbacks.cs). The interfaces are COM-style: a pointer to
 * a vtable, IUnknown in the first three slots, the methods in the order of 7-Zip's headers (CPP/7zip/Archive/IArchive.h,
 * CPP/7zip/IStream.h, CPP/7zip/IPassword.h). They are called here with unmanaged function pointers, as the Direct3D
 * renderer does, so no COM runtime is involved.
 *
 * The same code runs against 7-Zip's Linux build (7z.so) for the tests. Its interfaces have the same layout; the one
 * difference is that its wide characters (BSTR, wchar_t*) are 4 bytes instead of 2.
 */
internal static unsafe class SevenZipNative
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int E_FAIL = unchecked((int)0x80004005);

    // Item properties (CPP/7zip/PropID.h).
    public const uint kpidPath = 3;
    public const uint kpidName = 4;
    public const uint kpidIsDir = 6;
    public const uint kpidSize = 7;
    public const uint kpidPackSize = 8;
    public const uint kpidMTime = 12;
    public const uint kpidEncrypted = 15;
    public const uint kpidCRC = 19;
    public const uint kpidPhySize = 44;      // an archive property: the size of the archive itself

    // Property value types.
    public const ushort VT_EMPTY = 0;
    public const ushort VT_BSTR = 8;
    public const ushort VT_BOOL = 11;
    public const ushort VT_UI1 = 17;
    public const ushort VT_UI2 = 18;
    public const ushort VT_UI4 = 19;
    public const ushort VT_I8 = 20;
    public const ushort VT_UI8 = 21;
    public const ushort VT_FILETIME = 64;

    // Formats this app opens (the archive ids in 7-Zip's REGISTER_ARC lines).
    public const byte Format7z = 0x07;
    public const byte FormatRar = 0x03;
    public const byte FormatRar5 = 0xCC;

    /// <summary>{23170F69-40C1-278A-0000-000GG0SS0000}: a 7-Zip interface id from its group and number.</summary>
    public static Guid Iid(byte group, byte id) => new(0x23170F69, 0x40C1, 0x278A, 0, 0, 0, group, 0, id, 0, 0);

    /// <summary>{23170F69-40C1-278A-1000-000110FF0000}: the class id of a format's archive handler.</summary>
    public static Guid FormatClsid(byte format) => new(0x23170F69, 0x40C1, 0x278A, 0x10, 0x00, 0x00, 0x01, 0x10, format, 0x00, 0x00);

    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IID_ISequentialInStream = Iid(3, 0x01);
    public static readonly Guid IID_ISequentialOutStream = Iid(3, 0x02);
    public static readonly Guid IID_IInStream = Iid(3, 0x03);
    public static readonly Guid IID_ICryptoGetTextPassword = Iid(5, 0x10);
    public static readonly Guid IID_IProgress = Iid(0, 0x05);
    public static readonly Guid IID_IArchiveOpenCallback = Iid(6, 0x10);
    public static readonly Guid IID_IArchiveExtractCallback = Iid(6, 0x20);
    public static readonly Guid IID_IArchiveOpenVolumeCallback = Iid(6, 0x30);
    public static readonly Guid IID_IInArchive = Iid(6, 0x60);
    public static readonly Guid IID_ISetProperties = Iid(6, 0x03);

    /* ---------- loading the library ---------- */

    private static readonly Lazy<(nint Handle, string? Error)> Library = new(Load);

    /// <summary>
    /// 7z.dll next to the app on Windows. XANDRITE_7Z_LIB names another file (the tests point it at a Linux 7z.so).
    /// </summary>
    private static (nint, string?) Load()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("XANDRITE_7Z_LIB") is { Length: > 0 } custom) candidates.Add(custom);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "7z.dll" : "7z.so"));
        foreach (var path in candidates)
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out nint handle))
            {
                if (NativeLibrary.TryGetExport(handle, "CreateObject", out _)) return (handle, null);
                return (0, path + " is not 7-Zip's archive library (it has no CreateObject).");
            }
        }
        return (0, "7-Zip (" + (OperatingSystem.IsWindows() ? "7z.dll" : "7z.so") + ") was not found next to the app.");
    }

    public static bool IsAvailable => Library.Value.Handle != 0;

    /// <summary>Why 7-Zip cannot be used, or null if it can.</summary>
    public static string? LoadError => Library.Value.Error;

    private static delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int> CreateObjectFunction =>
        (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(Library.Value.Handle, "CreateObject");

    /// <summary>A new IInArchive for the format, or 0 if 7-Zip does not have it.</summary>
    public static nint CreateArchive(byte format)
    {
        if (!IsAvailable) throw new NotSupportedException(LoadError);
        Guid clsid = FormatClsid(format), iid = IID_IInArchive;
        nint archive = 0;
        int hr = CreateObjectFunction(&clsid, &iid, &archive);
        return hr == S_OK ? archive : 0;
    }

    /* ---------- IUnknown and IInArchive ---------- */

    private static void** Vtbl(nint p) => *(void***)p;

    public static int QueryInterface(nint p, Guid* iid, nint* result) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Vtbl(p)[0])(p, iid, result);

    /// <summary>
    /// Limits how many threads the handler decompresses with (7-Zip's "mt" switch), before the archive is opened.
    /// False if this handler has no settings (then it decides for itself).
    /// </summary>
    public static bool SetThreads(nint archive, int threads)
    {
        Guid iid = IID_ISetProperties;
        nint setter = 0;
        if (QueryInterface(archive, &iid, &setter) != S_OK || setter == 0) return false;
        try
        {
            nint name = Marshal.AllocHGlobal(3 * CharSize);   // "mt" and a terminator, as wide characters
            try
            {
                new Span<byte>((void*)name, 3 * CharSize).Clear();
                WriteWide(name, 0, 'm');
                WriteWide(name, 1, 't');
                PropVariant value = default;
                value.Type = VT_UI4;
                value.UInt32 = (uint)Math.Max(1, threads);
                nint* names = stackalloc nint[1];
                names[0] = name;
                // ISetProperties::SetProperties(const wchar_t *const *names, const PROPVARIANT *values, UInt32 count)
                int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, PropVariant*, uint, int>)Vtbl(setter)[3])(setter, names, &value, 1);
                return hr == S_OK;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }
        finally
        {
            Release(setter);
        }
    }

    private static void WriteWide(nint text, int index, char c)
    {
        if (CharSize == 2) ((ushort*)text)[index] = c;
        else ((uint*)text)[index] = c;
    }

    public static uint Release(nint p) => ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(p)[2])(p);

    public static int Open(nint archive, nint stream, ulong* maxCheckStartPosition, nint openCallback) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, ulong*, nint, int>)Vtbl(archive)[3])(archive, stream, maxCheckStartPosition, openCallback);

    public static int Close(nint archive) => ((delegate* unmanaged[Stdcall]<nint, int>)Vtbl(archive)[4])(archive);

    public static int GetNumberOfItems(nint archive, uint* count) =>
        ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Vtbl(archive)[5])(archive, count);

    public static int GetProperty(nint archive, uint index, uint propId, PropVariant* value) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, PropVariant*, int>)Vtbl(archive)[6])(archive, index, propId, value);

    public static int Extract(nint archive, uint* indices, uint count, int testMode, nint extractCallback) =>
        ((delegate* unmanaged[Stdcall]<nint, uint*, uint, int, nint, int>)Vtbl(archive)[7])(archive, indices, count, testMode, extractCallback);

    public static int GetArchiveProperty(nint archive, uint propId, PropVariant* value) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, PropVariant*, int>)Vtbl(archive)[8])(archive, propId, value);

    /* ---------- wide strings ---------- */

    /// <summary>Bytes per wide character: 2 on Windows, 4 (wchar_t) in 7-Zip's Linux build.</summary>
    public static int CharSize => OperatingSystem.IsWindows() ? 2 : 4;

    private static Encoding WideEncoding => OperatingSystem.IsWindows() ? Encoding.Unicode : Encoding.UTF32;

    /// <summary>A zero-terminated wide string.</summary>
    public static string ReadWide(nint text)
    {
        if (text == 0) return "";
        int length = 0;
        if (CharSize == 2) while (((ushort*)text)[length] != 0) length++;
        else while (((uint*)text)[length] != 0) length++;
        return WideEncoding.GetString((byte*)text, length * CharSize);
    }

    /// <summary>A BSTR allocated the way 7-Zip frees it (SysAllocString on Windows, malloc with a length prefix on Linux).</summary>
    public static nint AllocBstr(string value)
    {
        if (OperatingSystem.IsWindows()) return Marshal.StringToBSTR(value);
        byte[] text = WideEncoding.GetBytes(value);
        byte* block = (byte*)NativeMemory.Alloc((nuint)(4 + text.Length + CharSize));
        *(uint*)block = (uint)text.Length;
        text.CopyTo(new Span<byte>(block + 4, text.Length));
        new Span<byte>(block + 4 + text.Length, CharSize).Clear();
        return (nint)(block + 4);
    }

    public static void FreeBstr(nint bstr)
    {
        if (bstr == 0) return;
        if (OperatingSystem.IsWindows()) Marshal.FreeBSTR(bstr);
        else NativeMemory.Free((byte*)bstr - 4);
    }
}

/// <summary>
/// A PROPVARIANT: the type in the first two bytes, the value from byte 8. Windows' is 24 bytes, 7-Zip's Linux one 16;
/// this is large enough for both.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public ulong UInt64;
    [FieldOffset(8)] public uint UInt32;
    [FieldOffset(8)] public short Bool;   // VARIANT_BOOL: -1 true, 0 false
    [FieldOffset(8)] public nint Pointer;

    /// <summary>Frees what the value owns (only strings, for the properties read here).</summary>
    public void Clear()
    {
        if (Type == SevenZipNative.VT_BSTR) SevenZipNative.FreeBstr(Pointer);
        this = default;
    }

    public readonly ulong? AsUInt64 => Type switch
    {
        SevenZipNative.VT_UI8 or SevenZipNative.VT_I8 => UInt64,
        SevenZipNative.VT_UI4 => UInt32,
        SevenZipNative.VT_UI2 => (ushort)UInt32,
        SevenZipNative.VT_UI1 => (byte)UInt32,
        _ => null,
    };

    public readonly bool AsBool => Type == SevenZipNative.VT_BOOL && Bool != 0;

    /// <summary>A FILETIME as milliseconds since 1970, or null.</summary>
    public readonly double? AsUnixMs => Type == SevenZipNative.VT_FILETIME && UInt64 != 0
        ? (UInt64 - 116444736000000000UL) / 10000.0
        : null;
}
