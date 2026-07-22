using System;
using System.Globalization;
using System.Threading;

namespace CodexPortableManager.Tests
{
    internal static class PortableExitFixture
    {
        private static int Main()
        {
            int holdMilliseconds;
            if (int.TryParse(
                Environment.GetEnvironmentVariable("CPM_REGRESSION_CHILD_HOLD_MS"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out holdMilliseconds) && holdMilliseconds > 0)
            {
                Thread.Sleep(holdMilliseconds);
                return 0;
            }
            int exitCode;
            return int.TryParse(
                Environment.GetEnvironmentVariable("CPM_REGRESSION_CHILD_EXIT_CODE"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out exitCode)
                ? exitCode
                : 64;
        }
    }
}
