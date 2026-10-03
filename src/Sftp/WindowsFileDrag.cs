using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using TermThing.Diagnostics;

namespace TermThing.Sftp;

/// <summary>
/// A Windows "virtual file" drag source using OLE delayed rendering
/// (<c>CFSTR_FILEDESCRIPTORW</c> + <c>CFSTR_FILECONTENTS</c>). The dragged file's
/// bytes are produced only when the drop target (e.g. Explorer) actually asks for
/// them — i.e. on <b>drop</b>, not while the mouse is held. This avoids the freeze
/// caused by pre-downloading a (possibly large) SFTP file before the drag begins.
///
/// <para>Files only. Callers should handle directories some other way, and use a
/// real-file drag on non-Windows platforms.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileDrag
{
    /// <summary>
    /// Starts a synchronous OLE drag for one or more virtual files. Blocks (pumping the
    /// OLE message loop) until the drag completes. <paramref name="readContent"/> is
    /// invoked on drop with a file's index in <paramref name="files"/> to produce its bytes. Returns true if the drag was
    /// initiated (whether or not the user completed a drop); false if it could not be
    /// started (caller should fall back).
    /// </summary>
    public static bool TryDrag(IReadOnlyList<(string Name, long Size)> files, Func<int, byte[]> readContent)
    {
        if (files.Count == 0 || files.Any(f => string.IsNullOrEmpty(f.Name)) || readContent is null) return false;

        // OLE must be initialised on this (STA UI) thread. Avalonia already does this;
        // calling again is harmless (returns S_FALSE).
        OleInitialize(IntPtr.Zero);

        var data   = new VirtualFileDataObject(files, readContent);
        var source = new DropSource();

        int hr = DoDragDrop(data, source, DROPEFFECT_COPY, out _);
        // DRAGDROP_S_DROP (0x00040100) / DRAGDROP_S_CANCEL (0x00040101) are the normal
        // "success" returns; S_OK is also fine. Anything else means it never ran.
        return hr == 0 || hr == unchecked((int)0x00040100) || hr == unchecked((int)0x00040101);
    }

    // -----------------------------------------------------------------------
    // IDataObject implementing FILEDESCRIPTOR + FILECONTENTS delayed rendering
    // -----------------------------------------------------------------------

    /// <summary>
    /// <c>IDataObject</c> with every method <c>[PreserveSig]</c>. The BCL's
    /// <see cref="System.Runtime.InteropServices.ComTypes.IDataObject"/> declares
    /// <c>GetData</c> etc. as <c>void</c>, so rejecting a format means throwing — and
    /// Explorer probes dozens of formats during drag-over, each one a first-chance
    /// exception the debugger breaks on. Returning HRESULTs avoids that entirely.
    /// </summary>
    [ComImport, Guid("0000010E-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDataObject
    {
        [PreserveSig] int GetData(ref FORMATETC format, out STGMEDIUM medium);
        [PreserveSig] int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium);
        [PreserveSig] int QueryGetData(ref FORMATETC format);
        [PreserveSig] int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut);
        [PreserveSig] int SetData(ref FORMATETC formatIn, ref STGMEDIUM medium,
                                  [MarshalAs(UnmanagedType.Bool)] bool release);
        [PreserveSig] int EnumFormatEtc(DATADIR direction, out IEnumFORMATETC? enumFormat);
        [PreserveSig] int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink adviseSink, out int connection);
        [PreserveSig] int DUnadvise(int connection);
        [PreserveSig] int EnumDAdvise(out IEnumSTATDATA? enumAdvise);
    }

    private sealed class VirtualFileDataObject : IOleDataObject
    {
        private static readonly short CF_FILEDESCRIPTORW =
            (short)RegisterClipboardFormat("FileGroupDescriptorW");
        private static readonly short CF_FILECONTENTS =
            (short)RegisterClipboardFormat("FileContents");

        private readonly IReadOnlyList<(string Name, long Size)> _files;
        private readonly Func<int, byte[]> _readContent;

        public VirtualFileDataObject(IReadOnlyList<(string Name, long Size)> files, Func<int, byte[]> readContent)
        {
            _files       = files;
            _readContent = readContent;
        }

        // FILECONTENTS is requested per file by lindex; -1 is only unambiguous for one file.
        private int ContentIndex(in FORMATETC format)
            => format.lindex == -1 && _files.Count == 1 ? 0
             : format.lindex >= 0 && format.lindex < _files.Count ? format.lindex
             : -1;

        public int GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;
            if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0) return DV_E_TYMED;

            if (format.cfFormat == CF_FILEDESCRIPTORW)
            {
                medium.tymed          = TYMED.TYMED_HGLOBAL;
                medium.unionmember    = BuildFileGroupDescriptor();
                medium.pUnkForRelease = null;
                return S_OK;
            }

            if (format.cfFormat == CF_FILECONTENTS && ContentIndex(format) is var index and >= 0)
            {
                byte[] bytes;
                try { bytes = _readContent(index); } // produced on demand — i.e. on drop
                catch (Exception ex)
                {
                    Log.Warn("sftp", "Drag-out download failed", ex);
                    return E_FAIL;
                }
                medium.tymed          = TYMED.TYMED_HGLOBAL;
                medium.unionmember    = BytesToHGlobal(bytes);
                medium.pUnkForRelease = null;
                return S_OK;
            }

            return DV_E_FORMATETC;
        }

        public int QueryGetData(ref FORMATETC format)
        {
            if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0) return DV_E_TYMED;
            if (format.cfFormat == CF_FILEDESCRIPTORW) return S_OK;
            if (format.cfFormat == CF_FILECONTENTS && ContentIndex(format) >= 0) return S_OK;
            return DV_E_FORMATETC;
        }

        public int EnumFormatEtc(DATADIR direction, out IEnumFORMATETC? enumFormat)
        {
            if (direction != DATADIR.DATADIR_GET)
            {
                enumFormat = null;
                return E_NOTIMPL;
            }
            enumFormat = new FormatEtcEnumerator(new[]
            {
                MakeFormat(CF_FILEDESCRIPTORW, -1),
                MakeFormat(CF_FILECONTENTS,     0),
            });
            return S_OK;
        }

        public int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => E_NOTIMPL;

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = default;
            return E_NOTIMPL;
        }

        // Explorer calls SetData to hand back shell formats (drop description, performed
        // effect, ...). Declining is fine; we'd have to free medium only on success.
        public int SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => E_NOTIMPL;

        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return OLE_E_ADVISENOTSUPPORTED;
        }

        public int DUnadvise(int connection) => OLE_E_ADVISENOTSUPPORTED;

        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
        {
            enumAdvise = null;
            return OLE_E_ADVISENOTSUPPORTED;
        }

        private static FORMATETC MakeFormat(short cf, int lindex) => new()
        {
            cfFormat = cf,
            ptd      = IntPtr.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex   = lindex,
            tymed    = TYMED.TYMED_HGLOBAL,
        };

        private IntPtr BuildFileGroupDescriptor()
        {
            int fdSize   = Marshal.SizeOf<FILEDESCRIPTORW>();
            int total    = sizeof(uint) + fdSize * _files.Count; // cItems + descriptors
            IntPtr hMem  = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)total);
            IntPtr ptr   = GlobalLock(hMem);
            try
            {
                Marshal.WriteInt32(ptr, _files.Count); // cItems
                for (int i = 0; i < _files.Count; i++)
                {
                    var (name, size) = _files[i];
                    var fd = new FILEDESCRIPTORW
                    {
                        dwFlags          = FD_FILESIZE | FD_PROGRESSUI,
                        nFileSizeHigh    = (uint)(size >> 32),
                        nFileSizeLow     = (uint)(size & 0xFFFFFFFF),
                        cFileName        = new char[260],
                    };
                    var chars = name.Length > 259 ? name[..259] : name;
                    chars.CopyTo(0, fd.cFileName, 0, chars.Length);
                    Marshal.StructureToPtr(fd, ptr + sizeof(uint) + fdSize * i, false);
                }
            }
            finally { GlobalUnlock(hMem); }
            return hMem;
        }

        private static IntPtr BytesToHGlobal(byte[] bytes)
        {
            IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(bytes.Length, 1));
            IntPtr ptr  = GlobalLock(hMem);
            try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
            finally { GlobalUnlock(hMem); }
            return hMem;
        }
    }

    // -----------------------------------------------------------------------
    // IEnumFORMATETC
    // -----------------------------------------------------------------------

    private sealed class FormatEtcEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] _formats;
        private int _index;

        public FormatEtcEnumerator(FORMATETC[] formats, int index = 0)
        {
            _formats = formats;
            _index   = index;
        }

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            int fetched = 0;
            while (fetched < celt && _index < _formats.Length)
                rgelt[fetched++] = _formats[_index++];
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = fetched;
            return fetched == celt ? S_OK : S_FALSE;
        }

        public int Skip(int celt) { _index += celt; return _index <= _formats.Length ? S_OK : S_FALSE; }
        public int Reset() { _index = 0; return S_OK; }
        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEtcEnumerator(_formats, _index);
    }

    // -----------------------------------------------------------------------
    // IDropSource
    // -----------------------------------------------------------------------

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int fEscapePressed, uint grfKeyState);
        [PreserveSig] int GiveFeedback(uint dwEffect);
    }

    private sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(int fEscapePressed, uint grfKeyState)
        {
            if (fEscapePressed != 0) return DRAGDROP_S_CANCEL;
            // No mouse buttons still down → the user released → drop.
            if ((grfKeyState & (MK_LBUTTON | MK_RBUTTON)) == 0) return DRAGDROP_S_DROP;
            return S_OK;
        }

        public int GiveFeedback(uint dwEffect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    // -----------------------------------------------------------------------
    // Native structs / P-Invoke / constants
    // -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FILEDESCRIPTORW
    {
        public uint     dwFlags;
        public Guid     clsid;
        public int      sizelCx;
        public int      sizelCy;
        public int      pointlX;
        public int      pointlY;
        public uint     dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint     nFileSizeHigh;
        public uint     nFileSizeLow;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 260)]
        public char[]   cFileName;
    }

    private const uint FD_FILESIZE  = 0x00000040;
    private const uint FD_PROGRESSUI = 0x00004000;

    private const int  S_OK        = 0;
    private const int  S_FALSE     = 1;
    private const int  E_NOTIMPL   = unchecked((int)0x80004001);
    private const int  E_FAIL      = unchecked((int)0x80004005);
    private const int  DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int  DV_E_TYMED     = unchecked((int)0x80040069);
    private const int  OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);

    private const int  DRAGDROP_S_DROP   = unchecked((int)0x00040100);
    private const int  DRAGDROP_S_CANCEL = unchecked((int)0x00040101);
    private const int  DRAGDROP_S_USEDEFAULTCURSORS = unchecked((int)0x00040102);

    private const int  DROPEFFECT_COPY = 1;
    private const uint MK_LBUTTON = 0x0001;
    private const uint MK_RBUTTON = 0x0002;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(IOleDataObject pDataObj, IDropSource pDropSource,
        int dwOKEffect, out int pdwEffect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
