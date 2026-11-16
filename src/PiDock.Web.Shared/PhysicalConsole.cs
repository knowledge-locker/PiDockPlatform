using PiDock.Core;

namespace PiDock.Web.Shared;

internal sealed class PhysicalPresence { internal PhysicalPresence() { } }
internal static class PhysicalConsole
{
    internal static void RequireAttached() => PhysicalConsoleGuard.RequireAttached();
    internal static PhysicalPresence RequireRecovery() { PhysicalConsoleGuard.RequireRecovery(); return new(); }
}
