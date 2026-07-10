using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace TermThing.Sftp;

/// <summary>
/// A Windows "virtual file" drag source using OLE delayed rendering
/// (<c>CFSTR_FILEDESCRIPTORW</c> + <c>CFSTR_FILECONTENTS</c>). The dragged file's
/// bytes are produced only when the drop target (e.g. Explorer) actually asks for
/// them — i.e. on <b>drop</b>, not while the mouse is held. This avoids the freeze
/// caused by pre-downloading a (possibly large) SFTP file before the drag begins.
///
/// <para>Single-file only. Callers should fall back to a real-file drag for
/// directories / multi-selection, and for non-Windows platforms.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileDrag
{
    /// <summary>
    /// Starts a synchronous OLE drag for a single virtual file. Blocks (pumping the
    /// OLE message loop) until the drag completes. <paramref name="readContent"/> is
    /// invoked on drop to produce the file's bytes. Returns true if the drag was
    /// initiated (whether or not the user completed a drop); false if it could not be
    /// started (caller should fall back).
    /// </summary>
    public static bool TryDrag(string fileName, long size, Func<byte[]> readContent)
    {
        if (string.IsNullOrEmpty(fileName) || readContent is null) return false;

        // OLE must be initialised on this (STA UI) thread. Avalonia already does this;
        // calling again is harmless (returns S_FALSE).
        OleInitialize(IntPtr.Zero);

        var data   = new VirtualFileDataObject(fileName, size, readContent);
        var source = new DropSource();

        int hr = DoDragDrop(data, source, DROPEFFECT_COPY, out _);
        // DRAGDROP_S_DROP (0x00040100) / DRAGDROP_S_CANCEL (0x00040101) are the normal
        // "success" returns; S_OK is also fine. Anything else means it never ran.
        return hr == 0 || hr == unchecked((int)0x00040100) || hr == unchecked((int)0x00040101);
    }

    // -----------------------------------------------------------------------
    // IDataObject implementing FILEDESCRIPTOR + FILECONTENTS delayed rendering
    // -----------------------------------------------------------------------

    private sealed class VirtualFileDataObject : IDataObject
    {
        private static readonly short CF_FILEDESCRIPTORW =
            (short)RegisterClipboardFormat("FileGroupDescriptorW");
        private static readonly short CF_FILECONTENTS =
            (short)RegisterClipboardFormat("FileContents");

        private readonly string _fileName;
        private readonly long   _size;
        private readonly Func<byte[]> _readContent;

        public VirtualFileDataObject(string fileName, long size, Func<byte[]> readContent)
        {
            _fileName    = fileName;
            _size        = size;
            _readContent = readContent;
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;
            if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0)
                Marshal.ThrowExceptionForHR(DV_E_TYMED);

            if (format.cfFormat == CF_FILEDESCRIPTORW)
            {
                medium.tymed          = TYMED.TYMED_HGLOBAL;
                medium.unionmember    = BuildFileGroupDescriptor();
                medium.pUnkForRelease = null;
                return;
            }

            if (format.cfFormat == CF_FILECONTENTS && format.lindex is 0 or -1)
            {
                var bytes = _readContent(); // produced on demand — i.e. on drop
                medium.tymed          = TYMED.TYMED_HGLOBAL;
                medium.unionmember    = BytesToHGlobal(bytes);
                medium.pUnkForRelease = null;
                return;
            }

            Marshal.ThrowExceptionForHR(DV_E_FORMATETC);
        }

        public int QueryGetData(ref FORMATETC format)
        {
            if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0) return DV_E_TYMED;
            if (format.cfFormat == CF_FILEDESCRIPTORW) return S_OK;
            if (format.cfFormat == CF_FILECONTENTS && format.lindex is 0 or -1) return S_OK;
            return DV_E_FORMATETC;
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if (direction != DATADIR.DATADIR_GET)
                throw new NotImplementedException();
            return new FormatEtcEnumerator(new[]
            {
                MakeFormat(CF_FILEDESCRIPTORW, -1),
                MakeFormat(CF_FILECONTENTS,     0),
            });
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
            => Marshal.ThrowExceptionForHR(E_NOTIMPL);

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = default;
            return E_NOTIMPL;
        }

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
            => Marshal.ThrowExceptionForHR(E_NOTIMPL);

        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return OLE_E_ADVISENOTSUPPORTED;
        }

        public void DUnadvise(int connection)
            => Marshal.ThrowExceptionForHR(OLE_E_ADVISENOTSUPPORTED);

        public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
        {
            enumAdvise = null!;
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
            var fd = new FILEDESCRIPTORW
            {
                dwFlags          = FD_FILESIZE | FD_PROGRESSUI,
                nFileSizeHigh    = (uint)(_size >> 32),
                nFileSizeLow     = (uint)(_size & 0xFFFFFFFF),
                cFileName        = new char[260],
            };
            var chars = _fileName.Length > 259 ? _fileName[..259] : _fileName;
            chars.CopyTo(0, fd.cFileName, 0, chars.Length);

            int fdSize   = Marshal.SizeOf<FILEDESCRIPTORW>();
            int total    = sizeof(uint) + fdSize; // cItems + one descriptor
            IntPtr hMem  = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)total);
            IntPtr ptr   = GlobalLock(hMem);
            try
            {
                Marshal.WriteInt32(ptr, 1); // cItems = 1
                Marshal.StructureToPtr(fd, ptr + sizeof(uint), false);
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
    private static extern int DoDragDrop(IDataObject pDataObj, IDropSource pDropSource,
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
