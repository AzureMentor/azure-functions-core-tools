﻿using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Azure.Functions.Cli.Common
{
    public static class CommandChecker
    {
        public static bool CommandExists(string command)
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? CheckExitCode("where", command)
            : CheckExitCode("sh", $"-c \"command -v {command}\"");

        public static bool CommandExistsWindows(string command)
        {
            var process = Process.Start("where", command);
            process.WaitForExit();
            return process.ExitCode == 0;
        }

        private static bool CheckExitCode(string fileName, string args, int expectedExitCode = 0)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            var process = Process.Start(processStartInfo);
            process.WaitForExit();
            return process.ExitCode == 0;
        }
    }
}
