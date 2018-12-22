using System;
using System.Runtime.InteropServices;

namespace OpenSim.Framework.Console
{
    public static class ConsoleMode
    {
        const uint ENABLE_QUICK_EDIT = 0x0040;
        const uint ENABLE_EXTENDED_FLAGS = 0x0080;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        private const int STDIN_HANDLE = -10;
        internal static bool DisableQuickEdit()
        {
            IntPtr consoleHandle = GetStdHandle(STDIN_HANDLE);

            uint consoleMode;
            // get current console mode
            if (!GetConsoleMode(consoleHandle, out consoleMode))
                return false;

            // setting QUICK_EDIT mode either way requires EXTENDED
            consoleMode |= ENABLE_EXTENDED_FLAGS;
            consoleMode &= ~ENABLE_QUICK_EDIT;
            return SetConsoleMode(consoleHandle, consoleMode);
        }
    }
}