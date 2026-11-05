using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XppVsCode.Server.Tracing
{
    /// <summary>One event of an .etl file, valid only during the callback (the payload points into ETW's buffer).</summary>
    internal struct EtwEvent
    {
        public Guid Provider;
        public ushort Id;
        public int ThreadId;
        /// <summary>FILETIME (100 ns since 1601, UTC).</summary>
        public long Timestamp;
        public bool Is64Bit;
        public IntPtr UserData;
        public int UserDataLength;
    }

    /// <summary>Reads an .etl file with the Windows ETW consumer API (advapi32 OpenTrace/ProcessTrace), no other library.</summary>
    internal static class EtwFileReader
    {
        private const uint ProcessTraceModeEventRecord = 0x10000000;
        private const ulong InvalidHandle = ulong.MaxValue;
        private const ushort Header64BitFlag = 0x0040;

        // EVENT_TRACE_LOGFILEW on x64 (448 bytes); only the fields we set or read are declared.
        [StructLayout(LayoutKind.Explicit, Size = 448)]
        private struct EventTraceLogfile
        {
            [FieldOffset(0)] public IntPtr LogFileName;
            [FieldOffset(28)] public uint ProcessTraceMode;
            [FieldOffset(424)] public IntPtr EventRecordCallback;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void EventRecordCallback(IntPtr eventRecord);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern ulong OpenTraceW(ref EventTraceLogfile logfile);

        [DllImport("advapi32.dll")]
        private static extern int ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll")]
        private static extern int CloseTrace(ulong traceHandle);

        /// <summary>Calls <paramref name="onEvent"/> for every event of the file, in time order.</summary>
        public static void Read(string path, Action<EtwEvent> onEvent)
        {
            if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Reading traces needs the 64-bit server.");
            Exception callbackError = null;
            EventRecordCallback callback = record =>
            {
                if (callbackError != null) return;
                try
                {
                    // EVENT_RECORD (x64): EVENT_HEADER at 0, UserDataLength at 86, UserData at 96.
                    ushort flags = (ushort)Marshal.ReadInt16(record, 4);
                    var e = new EtwEvent
                    {
                        ThreadId = Marshal.ReadInt32(record, 8),
                        Timestamp = Marshal.ReadInt64(record, 16),
                        Provider = ReadGuid(record, 24),
                        Id = (ushort)Marshal.ReadInt16(record, 40),
                        Is64Bit = (flags & Header64BitFlag) != 0,
                        UserDataLength = (ushort)Marshal.ReadInt16(record, 86),
                        UserData = Marshal.ReadIntPtr(record, 96),
                    };
                    onEvent(e);
                }
                catch (Exception ex)
                {
                    callbackError = ex;
                }
            };

            IntPtr name = Marshal.StringToHGlobalUni(path);
            try
            {
                var logfile = new EventTraceLogfile
                {
                    LogFileName = name,
                    ProcessTraceMode = ProcessTraceModeEventRecord,
                    EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback),
                };
                ulong handle = OpenTraceW(ref logfile);
                if (handle == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error(), "Can't open the trace " + path);
                try
                {
                    int status = ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero);
                    if (status != 0) throw new Win32Exception(status, "Can't read the trace " + path);
                }
                finally
                {
                    CloseTrace(handle);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(name);
                GC.KeepAlive(callback);
            }
            if (callbackError != null) throw new InvalidOperationException("Reading the trace failed: " + callbackError.Message, callbackError);
        }

        private static Guid ReadGuid(IntPtr p, int offset)
        {
            var bytes = new byte[16];
            Marshal.Copy(p + offset, bytes, 0, 16);
            return new Guid(bytes);
        }
    }
}
