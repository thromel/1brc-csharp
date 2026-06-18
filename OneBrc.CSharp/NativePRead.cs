using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OneBrc.CSharp;

internal static unsafe partial class NativePRead
{
    private const int InterruptedSystemCall = 4;

    public static int Read(int fileDescriptor, byte* buffer, int byteCount, long offset)
    {
        while (true)
        {
            var read = PRead(fileDescriptor, buffer, (nuint)byteCount, offset);
            if (read >= 0)
            {
                return checked((int)read);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == InterruptedSystemCall)
            {
                continue;
            }

            throw new IOException("pread failed.", new Win32Exception(error));
        }
    }

    public static int ReadFull(int fileDescriptor, byte* buffer, int byteCount, long offset)
    {
        var total = 0;
        while (total < byteCount)
        {
            var read = Read(fileDescriptor, buffer + total, byteCount - total, offset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    [LibraryImport("libSystem.B.dylib", EntryPoint = "pread", SetLastError = true)]
    private static partial nint PRead(int fileDescriptor, void* buffer, nuint byteCount, long offset);
}
