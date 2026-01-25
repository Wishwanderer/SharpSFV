using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Windows.Forms;

namespace SharpSFV.Interop
{
    // --- TASKBAR ENUMS ---
    public enum TbpFlag
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8
    }

    /// <summary>
    /// COM Import for the Windows Taskbar instance.
    /// Used to display progress (Green/Red/Yellow) on the application icon in the taskbar.
    /// </summary>
    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    public class TaskbarInstance
    {
    }

    /// <summary>
    /// Interface definition for ITaskbarList3 (Windows 7+).
    /// Allows controlling the progress bar embedded in the taskbar icon.
    /// </summary>
    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITaskbarList3
    {
        // ITaskbarList Methods
        [PreserveSig] void HrInit();
        [PreserveSig] void AddTab(IntPtr hwnd);
        [PreserveSig] void DeleteTab(IntPtr hwnd);
        [PreserveSig] void ActivateTab(IntPtr hwnd);
        [PreserveSig] void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2 Methods
        [PreserveSig] void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3 Methods
        [PreserveSig] void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        [PreserveSig] void SetProgressState(IntPtr hwnd, TbpFlag tbpFlags);
        [PreserveSig] void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        [PreserveSig] void UnregisterTab(IntPtr hwndTab);
        [PreserveSig] void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        [PreserveSig] void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
        [PreserveSig] void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        [PreserveSig] void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        [PreserveSig] void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        [PreserveSig] void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
        [PreserveSig] void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
        [PreserveSig] void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    /// <summary>
    /// Static container for Native Windows API calls (Kernel32/User32).
    /// Handles low-level Disk I/O, Console attachment, and UI message sending.
    /// </summary>
    internal static class Win32Storage
    {
        // --- File Access Constants ---
        public const uint GENERIC_READ = 0x80000000;
        public const uint FILE_SHARE_READ = 0x00000001;
        public const uint FILE_SHARE_WRITE = 0x00000002; // Allow others to write while we read (non-locking)
        public const uint OPEN_EXISTING = 3;

        // --- Console Constants ---
        // Used to attach a GUI app to the parent command line window (Headless mode)
        public const int ATTACH_PARENT_PROCESS = -1;

        // --- IOCTL (Device I/O Control) Codes ---
        // Magic numbers to query hardware drivers directly
        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;         // Ask about RPM/Seek Penalty
        public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000; // Ask which Physical Disk a Volume is on

        // --- Property IDs ---
        public const int StorageDeviceProperty = 0;
        public const int StorageDeviceSeekPenaltyProperty = 7; // The specific question: "Is there a seek penalty?" (HDD vs SSD)
        public const int PropertyStandardQuery = 0;

        // --- Window Messages ---
        public const int WM_SETREDRAW = 0x000B; // Stops a control from repainting itself

        // --- Progress Bar Messages ---
        public const int PBM_SETSTATE = 0x0410;
        public const int PBST_NORMAL = 1; // Green
        public const int PBST_ERROR = 2;  // Red
        public const int PBST_PAUSED = 3; // Yellow

        // --- Native Structures ---
        // These must match the binary layout of C++ structs expected by Windows API.

        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            public byte AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_EXTENT
        {
            public int DiskNumber;
            public long StartingOffset;
            public long ExtentLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VOLUME_DISK_EXTENTS
        {
            public int NumberOfDiskExtents;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public DISK_EXTENT[] Extents;
        }

        // --- P/Invokes: Console Management ---

        /// <summary>
        /// Attaches the calling process to the console of the specified process.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        /// <summary>
        /// Detaches the calling process from its console.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeConsole();

        // --- P/Invokes: Low-Level I/O ---

        /// <summary>
        /// Creates or opens a file, file stream, or device.
        /// Used here to open handles to physical drives (e.g. "\\.\PhysicalDrive0") which .NET IO cannot do.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        /// <summary>
        /// Sends a control code directly to a specified device driver.
        /// Used to query hardware characteristics (RPM, Seek Penalty).
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            ref STORAGE_PROPERTY_QUERY lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        // --- P/Invokes: User Interface ---

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // --- Optimization Helpers ---

        /// <summary>
        /// Stops a control from redrawing itself.
        /// <para>
        /// <b>Usage:</b> Call this before adding/removing many items (e.g., 5000+ files).
        /// This prevents the OS from attempting to repaint the list 5000 times, speeding up the operation by ~100x.
        /// </para>
        /// </summary>
        public static void SuspendDrawing(Control control)
        {
            SendMessage(control.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Re-enables drawing and forces a repaint.
        /// Must be called in a <c>finally</c> block after <see cref="SuspendDrawing"/>.
        /// </summary>
        public static void ResumeDrawing(Control control)
        {
            SendMessage(control.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            control.Refresh();
        }

        /// <summary>
        /// Changes the color of a standard WinForms ProgressBar using Windows messages.
        /// (1=Green, 2=Red, 3=Yellow).
        /// </summary>
        public static void SetProgressBarState(ProgressBar pBar, int state)
        {
            SendMessage(pBar.Handle, PBM_SETSTATE, new IntPtr(state), IntPtr.Zero);
        }
    }
}