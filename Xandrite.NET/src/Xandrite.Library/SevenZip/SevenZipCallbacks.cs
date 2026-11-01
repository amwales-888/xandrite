using Xandrite.Library.Logging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Xandrite.Library.SevenZip.SevenZipNative;

namespace Xandrite.Library.SevenZip;

/// <summary>
/// A .NET object that 7-Zip can call: for each interface it implements, a small native block holding a vtable pointer
/// and a handle back to the object. Interfaces with the same vtable layout (IInStream extends ISequentialInStream) share
/// a block. 7-Zip's AddRef and Release are counted, but the memory is only freed by <see cref="Dispose"/>, once the
/// archive that used the object has been closed and released.
///
/// Exceptions never cross into 7-Zip: a callback that fails records the exception and returns an error code, and the
/// caller rethrows it when 7-Zip returns (see <see cref="Failure"/>).
/// </summary>
internal abstract unsafe class NativeObject : IDisposable
{
    private readonly List<(Guid[] Iids, nint Block)> _blocks = [];
    private GCHandle _self;
    private int _references;

    protected NativeObject() => _self = GCHandle.Alloc(this);

    /// <summary>The first exception thrown inside a callback, to rethrow once 7-Zip has returned.</summary>
    public Exception? Failure { get; private set; }

    protected void Fail(Exception e) => Failure ??= e;

    protected nint AddInterface(void** vtable, params Guid[] iids)
    {
        var block = (nint*)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
        block[0] = (nint)vtable;
        block[1] = GCHandle.ToIntPtr(_self);
        _blocks.Add((iids, (nint)block));
        return (nint)block;
    }

    /// <summary>The pointer to hand to 7-Zip for an interface. It counts as a reference, as COM expects.</summary>
    public nint Pointer(Guid iid)
    {
        nint p = Find(iid);
        if (p == 0) throw new InvalidOperationException("Interface not implemented: " + iid);
        Interlocked.Increment(ref _references);
        return p;
    }

    private nint Find(Guid iid)
    {
        if (iid == IID_IUnknown) return _blocks[0].Block;
        foreach (var (iids, block) in _blocks)
            if (iids.Contains(iid)) return block;
        return 0;
    }

    protected static T Self<T>(nint block) where T : NativeObject => (T)GCHandle.FromIntPtr(((nint*)block)[1]).Target!;

    /// <summary>Builds a vtable: IUnknown's three methods followed by the given ones.</summary>
    protected static void** Vtable(params nint[] methods)
    {
        var table = (void**)NativeMemory.Alloc((nuint)((3 + methods.Length) * sizeof(nint)));
        table[0] = (void*)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        table[1] = (void*)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        table[2] = (void*)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
        for (int i = 0; i < methods.Length; i++) table[3 + i] = (void*)methods[i];
        return table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(nint block, Guid* iid, nint* result)
    {
        var self = Self<NativeObject>(block);
        nint p = self.Find(*iid);
        *result = p;
        if (p == 0) return E_NOINTERFACE;
        Interlocked.Increment(ref self._references);
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint block) => (uint)Interlocked.Increment(ref Self<NativeObject>(block)._references);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint block) => (uint)Math.Max(0, Interlocked.Decrement(ref Self<NativeObject>(block)._references));

    public virtual void Dispose()
    {
        foreach (var (_, block) in _blocks) NativeMemory.Free((void*)block);
        _blocks.Clear();
        if (_self.IsAllocated) _self.Free();
    }
}

/// <summary>IInStream (and ISequentialInStream) over a .NET stream: how 7-Zip reads the archive.</summary>
internal sealed unsafe class InStream : NativeObject
{
    private static readonly void** Table = Vtable(
        (nint)(delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)&Read,
        (nint)(delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)&Seek);

    public InStream(Stream stream)
    {
        Stream = stream;
        AddInterface(Table, IID_ISequentialInStream, IID_IInStream);
    }

    public Stream Stream { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Read(nint block, byte* data, uint size, uint* processed)
    {
        var self = Self<InStream>(block);
        try
        {
            int total = 0;
            while (total < size)   // 7-Zip accepts short reads, but full ones are simpler for it and just as cheap here
            {
                int n = self.Stream.Read(new Span<byte>(data + total, (int)(size - total)));
                if (n == 0) break;
                total += n;
            }
            if (processed != null) *processed = (uint)total;
            return S_OK;
        }
        catch (Exception e)
        {
            self.Fail(e);
            if (processed != null) *processed = 0;
            return E_FAIL;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Seek(nint block, long offset, uint origin, ulong* newPosition)
    {
        var self = Self<InStream>(block);
        try
        {
            long position = self.Stream.Seek(offset, (SeekOrigin)origin);
            if (newPosition != null) *newPosition = (ulong)position;
            return S_OK;
        }
        catch (Exception e)
        {
            self.Fail(e);
            return E_FAIL;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        Stream.Dispose();
    }
}

/// <summary>
/// What 7-Zip asks while opening an archive: progress (ignored), the other volumes of a multi-volume RAR (found next to
/// the first one), and a password (refused, so a protected archive fails to open instead of waiting for one).
/// </summary>
internal sealed unsafe class OpenCallback : NativeObject
{
    private readonly IoMeter? _meter = IoMeter.Current;

    private static readonly void** OpenTable = Vtable(
        (nint)(delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)&SetTotal,
        (nint)(delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)&SetCompleted);

    private static readonly void** VolumeTable = Vtable(
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, PropVariant*, int>)&GetProperty,
        (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetStream);

    private static readonly void** PasswordTable = Vtable(
        (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&CryptoGetTextPassword);

    private readonly string _firstVolume;
    private readonly List<InStream> _volumes = [];

    public OpenCallback(string firstVolume)
    {
        _firstVolume = firstVolume;
        AddInterface(OpenTable, IID_IArchiveOpenCallback);
        AddInterface(VolumeTable, IID_IArchiveOpenVolumeCallback);
        AddInterface(PasswordTable, IID_ICryptoGetTextPassword);
    }

    public bool PasswordRequested { get; private set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetTotal(nint block, ulong* files, ulong* bytes) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetCompleted(nint block, ulong* files, ulong* bytes) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetProperty(nint block, uint propId, PropVariant* value)
    {
        var self = Self<OpenCallback>(block);
        *value = default;
        if (propId == kpidName)
        {
            value->Type = VT_BSTR;
            value->Pointer = AllocBstr(Path.GetFileName(self._firstVolume));
        }
        return S_OK;
    }

    /// <summary>Another volume, by name. Only files in the first volume's own folder are opened.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetStream(nint block, nint name, nint* stream)
    {
        var self = Self<OpenCallback>(block);
        *stream = 0;
        try
        {
            string file = Path.GetFileName(ReadWide(name));
            string path = Path.Combine(Path.GetDirectoryName(self._firstVolume) ?? "", file);
            if (file.Length == 0 || !File.Exists(path)) return S_FALSE;   // no such volume: 7-Zip reports the set as incomplete
            Stream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            if (self._meter is { } meter) input = new MeteredStream(input, meter, path);   // 7-Zip's thread: use the meter of whoever opened the set
            var volume = new InStream(input);
            self._volumes.Add(volume);
            *stream = volume.Pointer(IID_IInStream);
            return S_OK;
        }
        catch (Exception e)
        {
            self.Fail(e);
            return E_FAIL;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CryptoGetTextPassword(nint block, nint* password)
    {
        Self<OpenCallback>(block).PasswordRequested = true;
        *password = 0;
        return E_ABORT;
    }

    public override void Dispose()
    {
        foreach (var volume in _volumes) volume.Dispose();
        _volumes.Clear();
        base.Dispose();
    }
}

/// <summary>
/// Receives extracted files. 7-Zip goes through the requested items in archive order, in one pass (which is what makes
/// solid archives fast): for each it asks for an output stream, writes the content, and reports the result. Each wanted
/// item is collected in memory and handed to <c>completed</c> with 7-Zip's result code.
/// </summary>
internal sealed unsafe class ExtractCallback : NativeObject
{
    public const int AskExtract = 0;
    public const int ResultOk = 0;

    private static readonly void** ExtractTable = Vtable(
        (nint)(delegate* unmanaged[Stdcall]<nint, ulong, int>)&SetTotal,
        (nint)(delegate* unmanaged[Stdcall]<nint, ulong*, int>)&SetCompleted,
        (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int, int>)&GetStream,
        (nint)(delegate* unmanaged[Stdcall]<nint, int, int>)&PrepareOperation,
        (nint)(delegate* unmanaged[Stdcall]<nint, int, int>)&SetOperationResult);

    private static readonly void** PasswordTable = Vtable(
        (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&CryptoGetTextPassword);

    private readonly Func<uint, bool> _wanted;
    private readonly Action<uint, MemoryStream?, int, bool> _completed;
    private readonly long _maxBytes;
    private readonly CancellationToken _cancellation;
    private readonly OutStream _out;
    private uint _current;
    private bool _currentWanted;
    private MemoryStream? _buffer;
    private bool _tooLarge;

    /// <param name="completed">(index, content or null, 7-Zip result code, too large).</param>
    public ExtractCallback(Func<uint, bool> wanted, Action<uint, MemoryStream?, int, bool> completed, long maxBytes, CancellationToken cancellation)
    {
        _wanted = wanted;
        _completed = completed;
        _maxBytes = maxBytes;
        _cancellation = cancellation;
        _out = new OutStream(this);
        AddInterface(ExtractTable, IID_IProgress, IID_IArchiveExtractCallback);
        AddInterface(PasswordTable, IID_ICryptoGetTextPassword);
    }

    public bool PasswordRequested { get; private set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetTotal(nint block, ulong total) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetCompleted(nint block, ulong* completed) =>
        Self<ExtractCallback>(block)._cancellation.IsCancellationRequested ? E_ABORT : S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetStream(nint block, uint index, nint* stream, int askMode)
    {
        var self = Self<ExtractCallback>(block);
        *stream = 0;
        if (self._cancellation.IsCancellationRequested) return E_ABORT;
        self._current = index;
        self._currentWanted = askMode == AskExtract && self._wanted(index);
        self._tooLarge = false;
        self._buffer = null;
        if (self._currentWanted)
        {
            self._buffer = new MemoryStream();
            *stream = self._out.Pointer(IID_ISequentialOutStream);
        }
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PrepareOperation(nint block, int askMode) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetOperationResult(nint block, int result)
    {
        var self = Self<ExtractCallback>(block);
        if (!self._currentWanted) return S_OK;
        var buffer = self._buffer;
        self._buffer = null;
        self._currentWanted = false;
        try
        {
            self._completed(self._current, result == ResultOk && !self._tooLarge ? buffer : null, result, self._tooLarge);
            return S_OK;
        }
        catch (Exception e)
        {
            self.Fail(e);   // rethrown when Extract returns (cancelled, or "stop reading" from a download)
            return E_ABORT;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CryptoGetTextPassword(nint block, nint* password)
    {
        Self<ExtractCallback>(block).PasswordRequested = true;
        *password = 0;
        return E_ABORT;
    }

    private int Write(byte* data, uint size, uint* processed)
    {
        if (processed != null) *processed = size;
        if (_buffer is null || _tooLarge) return S_OK;
        if (_buffer.Length + size > _maxBytes)
        {
            _tooLarge = true;   // keep going (the rest of a solid block may be wanted), but drop this one
            _buffer = null;
            return S_OK;
        }
        _buffer.Write(new ReadOnlySpan<byte>(data, (int)size));
        return S_OK;
    }

    public override void Dispose()
    {
        _out.Dispose();
        base.Dispose();
    }

    /// <summary>ISequentialOutStream: where 7-Zip writes an item's content.</summary>
    private sealed class OutStream : NativeObject
    {
        private static readonly void** Table = Vtable(
            (nint)(delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)&Write);

        private readonly ExtractCallback _owner;

        public OutStream(ExtractCallback owner)
        {
            _owner = owner;
            AddInterface(Table, IID_ISequentialOutStream);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Write(nint block, byte* data, uint size, uint* processed)
        {
            var self = Self<OutStream>(block);
            try
            {
                return self._owner.Write(data, size, processed);
            }
            catch (Exception e)
            {
                self._owner.Fail(e);
                return E_FAIL;
            }
        }
    }
}

/// <summary>The parts of a split archive (name.7z.001, name.7z.002 ...) read as one stream.</summary>
internal sealed class SplitStream : Stream
{
    private readonly FileStream[] _parts;
    private readonly long[] _starts;
    private long _position;

    public SplitStream(IReadOnlyList<string> paths)
    {
        _parts = paths.Select(p => IoMeter.OpenFile(p, 1 << 16)).ToArray();
        _starts = new long[_parts.Length + 1];
        for (int i = 0; i < _parts.Length; i++) _starts[i + 1] = _starts[i] + _parts[i].Length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _starts[^1];

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.IsEmpty) return 0;
        int part = Array.FindLastIndex(_starts, _parts.Length - 1, _parts.Length, s => s <= _position);   // searched backwards from the last part
        var stream = _parts[part];
        stream.Position = _position - _starts[part];
        int n = stream.Read(buffer[..(int)Math.Min(buffer.Length, _starts[part + 1] - _position)]);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        if (target < 0) throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        return _position = target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var part in _parts) part.Dispose();
        base.Dispose(disposing);
    }
}
