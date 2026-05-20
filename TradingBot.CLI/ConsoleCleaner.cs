using System.Runtime.InteropServices;

internal static class ConsoleCleaner
{
    private const int StdOutputHandle = -11;

    public static bool ClearLikeCls()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        if (!GetConsoleScreenBufferInfo(handle, out var info))
        {
            return false;
        }

        var cellCount = info.Size.X * info.Size.Y;
        var origin = new Coord(0, 0);

        return FillConsoleOutputCharacter(handle, ' ', cellCount, origin, out _)
            && FillConsoleOutputAttribute(handle, info.Attributes, cellCount, origin, out _)
            && SetConsoleCursorPosition(handle, origin);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out ConsoleScreenBufferInfo lpConsoleScreenBufferInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillConsoleOutputCharacter(
        IntPtr hConsoleOutput,
        char cCharacter,
        int nLength,
        Coord dwWriteCoord,
        out int lpNumberOfCharsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillConsoleOutputAttribute(
        IntPtr hConsoleOutput,
        short wAttribute,
        int nLength,
        Coord dwWriteCoord,
        out int lpNumberOfAttrsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCursorPosition(IntPtr hConsoleOutput, Coord dwCursorPosition);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SmallRect
    {
        public readonly short Left;
        public readonly short Top;
        public readonly short Right;
        public readonly short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ConsoleScreenBufferInfo
    {
        public readonly Coord Size;
        public readonly Coord CursorPosition;
        public readonly short Attributes;
        public readonly SmallRect Window;
        public readonly Coord MaximumWindowSize;
    }
}
