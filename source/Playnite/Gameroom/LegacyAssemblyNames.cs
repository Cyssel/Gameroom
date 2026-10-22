using System;
using System.Reflection;

namespace Playnite.Gameroom
{
    /// <summary>
    /// Gameroom ships its executables as Gameroom.exe and Gameroom.Fullscreen.exe, while themes and
    /// extensions made for Playnite reference them as Playnite.DesktopApp and Playnite.FullscreenApp.
    /// This maps those old assembly names to the running executable so existing add-ons keep working.
    /// </summary>
    public static class LegacyAssemblyNames
    {
        private static string legacyName;

        public static void Register(string legacyEntryAssemblyName)
        {
            if (legacyName != null)
            {
                return;
            }

            legacyName = legacyEntryAssemblyName;
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (string.Equals(name, legacyName, StringComparison.OrdinalIgnoreCase))
            {
                return Assembly.GetEntryAssembly();
            }

            return null;
        }
    }
}
