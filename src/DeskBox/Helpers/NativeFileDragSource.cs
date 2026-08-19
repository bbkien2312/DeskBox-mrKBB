using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Helpers;

/// <summary>
/// Starts a real shell/OLE file drag for paths that WinRT cannot expose as
/// StorageItems (most notably shortcuts). This keeps Explorer's normal move
/// semantics, so a drag to another folder is not redirected to Desktop.
/// </summary>
internal static class NativeFileDragSource
{
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int DRAGDROP_S_DROP = 0x00040100;
    private const int DRAGDROP_S_CANCEL = 0x00040101;
    private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const ushort CF_HDROP = 15;
    private const uint TYMED_HGLOBAL = 1;
    private const uint DVASPECT_CONTENT = 1;
    private const uint DROPEFFECT_COPY = 1;
    private const uint DROPEFFECT_MOVE = 2;
    private const uint DROPEFFECT_LINK = 4;
    private const uint MK_LBUTTON = 1;
    private const uint MK_RBUTTON = 2;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static readonly ushort PreferredDropEffectFormat =
        (ushort)RegisterClipboardFormatW("Preferred DropEffect");

    public static bool TryRun(
        IntPtr ownerHwnd,
        IReadOnlyList<string> paths,
        out uint effect)
    {
        effect = 0;
        string[] existingPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();
        if (existingPaths.Length == 0)
        {
            return false;
        }

        try
        {
            using var dataObject = new NativeFileDataObject(existingPaths);
            var dropSource = new NativeDropSource();
            int hr = DoDragDrop(
                dataObject,
                dropSource,
                DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK,
                out effect);
            bool completed = hr == DRAGDROP_S_DROP && effect != 0;
            App.Log(
                $"[NativeDrag] completed={completed} hr=0x{hr:X8} " +
                $"effect={effect} paths={existingPaths.Length}");
            return completed;
        }
        catch (Exception ex)
        {
            App.Log($"[NativeDrag] failed: {ex}");
            return false;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(
        [MarshalAs(UnmanagedType.Interface)] object dataObject,
        [MarshalAs(UnmanagedType.Interface)] object dropSource,
        uint allowedEffects,
        out uint effect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalFree(IntPtr memory);

    [ComVisible(true)]
    [Guid("0000010E-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDataObject
    {
        [PreserveSig] int GetData(ref FormatEtc format, out StgMedium medium);
        [PreserveSig] int GetDataHere(ref FormatEtc format, ref StgMedium medium);
        [PreserveSig] int QueryGetData(ref FormatEtc format);
        [PreserveSig] int GetCanonicalFormatEtc(ref FormatEtc input, out FormatEtc output);
        [PreserveSig] int SetData(ref FormatEtc format, ref StgMedium medium, bool release);
        [PreserveSig] int EnumFormatEtc(uint direction, out IntPtr enumerator);
        [PreserveSig] int DAdvise(ref FormatEtc format, uint flags, IntPtr sink, out uint connection);
        [PreserveSig] int DUnadvise(uint connection);
        [PreserveSig] int EnumDAdvise(out IntPtr enumerator);
    }

    [ComVisible(true)]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDropSource
    {
        [PreserveSig] int QueryContinueDrag(bool escapePressed, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    [ComVisible(true)]
    private sealed class NativeDropSource : IOleDropSource
    {
        public int QueryContinueDrag(bool escapePressed, uint keyState)
        {
            if (escapePressed)
            {
                return DRAGDROP_S_CANCEL;
            }

            return (keyState & (MK_LBUTTON | MK_RBUTTON)) == 0
                ? DRAGDROP_S_DROP
                : S_OK;
        }

        public int GiveFeedback(uint effect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    [ComVisible(true)]
    private sealed class NativeFileDataObject : IOleDataObject, IDisposable
    {
        private readonly string[] _paths;

        public NativeFileDataObject(string[] paths)
        {
            _paths = paths;
        }

        public int GetData(ref FormatEtc format, out StgMedium medium)
        {
            medium = default;
            if (format.tymed != TYMED_HGLOBAL ||
                (format.cfFormat != CF_HDROP &&
                 format.cfFormat != PreferredDropEffectFormat))
            {
                return DV_E_FORMATETC;
            }

            IntPtr data = format.cfFormat == CF_HDROP
                ? CreateHDrop(_paths)
                : CreateUInt32(DROPEFFECT_MOVE);
            if (data == IntPtr.Zero)
            {
                return unchecked((int)0x8007000E); // E_OUTOFMEMORY
            }

            medium.tymed = TYMED_HGLOBAL;
            medium.unionMember = data;
            medium.unkForRelease = IntPtr.Zero;
            return S_OK;
        }

        public int QueryGetData(ref FormatEtc format)
        {
            return format.tymed == TYMED_HGLOBAL &&
                   (format.cfFormat == CF_HDROP ||
                    format.cfFormat == PreferredDropEffectFormat)
                ? S_OK
                : DV_E_FORMATETC;
        }

        public int EnumFormatEtc(uint direction, out IntPtr enumerator)
        {
            enumerator = IntPtr.Zero;
            if (direction != 1) // DATADIR_GET
            {
                return E_NOTIMPL;
            }

            var formats = new[]
            {
                new FormatEtc
                {
                    cfFormat = CF_HDROP,
                    dwAspect = DVASPECT_CONTENT,
                    lindex = -1,
                    tymed = TYMED_HGLOBAL
                },
                new FormatEtc
                {
                    cfFormat = PreferredDropEffectFormat,
                    dwAspect = DVASPECT_CONTENT,
                    lindex = -1,
                    tymed = TYMED_HGLOBAL
                }
            };
            enumerator = Marshal.GetIUnknownForObject(new FormatEnumerator(formats));
            return S_OK;
        }

        public int GetDataHere(ref FormatEtc format, ref StgMedium medium) => E_NOTIMPL;
        public int GetCanonicalFormatEtc(ref FormatEtc input, out FormatEtc output)
        {
            output = default;
            return E_NOTIMPL;
        }
        public int SetData(ref FormatEtc format, ref StgMedium medium, bool release) => E_NOTIMPL;
        public int DAdvise(ref FormatEtc format, uint flags, IntPtr sink, out uint connection)
        {
            connection = 0;
            return E_NOTIMPL;
        }
        public int DUnadvise(uint connection) => E_NOTIMPL;
        public int EnumDAdvise(out IntPtr enumerator)
        {
            enumerator = IntPtr.Zero;
            return E_NOTIMPL;
        }

        public void Dispose()
        {
        }

        private static IntPtr CreateHDrop(IReadOnlyList<string> paths)
        {
            string payload = string.Join('\0', paths) + "\0\0";
            byte[] encodedPaths = Encoding.Unicode.GetBytes(payload);
            const int dropFilesSize = 20;
            IntPtr memory = GlobalAlloc(
                GMEM_MOVEABLE,
                (UIntPtr)(dropFilesSize + encodedPaths.Length));
            if (memory == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr locked = GlobalLock(memory);
            if (locked == IntPtr.Zero)
            {
                GlobalFree(memory);
                return IntPtr.Zero;
            }

            try
            {
                Marshal.WriteInt32(locked, 0, dropFilesSize);
                Marshal.WriteInt32(locked, 4, 0);
                Marshal.WriteInt32(locked, 8, 0);
                Marshal.WriteInt32(locked, 12, 0);
                Marshal.WriteInt32(locked, 16, 1); // fWide
                Marshal.Copy(encodedPaths, 0, IntPtr.Add(locked, dropFilesSize), encodedPaths.Length);
                return memory;
            }
            finally
            {
                GlobalUnlock(memory);
            }
        }

        private static IntPtr CreateUInt32(uint value)
        {
            IntPtr memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)sizeof(uint));
            if (memory == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr locked = GlobalLock(memory);
            if (locked == IntPtr.Zero)
            {
                GlobalFree(memory);
                return IntPtr.Zero;
            }

            try
            {
                Marshal.WriteInt32(locked, unchecked((int)value));
                return memory;
            }
            finally
            {
                GlobalUnlock(memory);
            }
        }
    }

    [ComVisible(true)]
    [Guid("00000103-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFormatEnumerator
    {
        [PreserveSig] int Next(uint count, [Out] FormatEtc[] formats, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IntPtr enumerator);
    }

    [ComVisible(true)]
    private sealed class FormatEnumerator : IFormatEnumerator
    {
        private readonly FormatEtc[] _formats;
        private int _index;

        public FormatEnumerator(FormatEtc[] formats) => _formats = formats;

        public int Next(uint count, FormatEtc[] formats, out uint fetched)
        {
            fetched = 0;
            while (fetched < count && _index < _formats.Length)
            {
                formats[fetched++] = _formats[_index++];
            }
            return fetched == count ? S_OK : S_FALSE;
        }

        public int Skip(uint count)
        {
            _index = Math.Min(_formats.Length, _index + (int)count);
            return _index < _formats.Length ? S_OK : S_FALSE;
        }

        public int Reset()
        {
            _index = 0;
            return S_OK;
        }

        public int Clone(out IntPtr enumerator)
        {
            var clone = new FormatEnumerator(_formats) { _index = _index };
            enumerator = Marshal.GetIUnknownForObject(clone);
            return S_OK;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormatEtc
    {
        public ushort cfFormat;
        public IntPtr ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StgMedium
    {
        public uint tymed;
        public IntPtr unionMember;
        public IntPtr unkForRelease;
    }
}
