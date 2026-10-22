using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Gameroom.Updater
{
    internal class UpdaterOptions
    {
        /// <summary>Download and install right away (started by Gameroom or after clicking Update).</summary>
        public bool Install { get; set; }
        /// <summary>Release to install, latest when empty.</summary>
        public string Version { get; set; }
        /// <summary>Gameroom process that is closing to let the update run.</summary>
        public int WaitPid { get; set; }
        /// <summary>Start Gameroom in Fullscreen mode after updating.</summary>
        public bool Fullscreen { get; set; }
        /// <summary>Running from the temporary copy.</summary>
        public bool Relocated { get; set; }

        public static UpdaterOptions Parse(string[] args)
        {
            var options = new UpdaterOptions();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--install":
                        options.Install = true;
                        break;
                    case "--version" when i + 1 < args.Length:
                        options.Version = args[++i];
                        break;
                    case "--wait-pid" when i + 1 < args.Length:
                        int.TryParse(args[++i], out var pid);
                        options.WaitPid = pid;
                        break;
                    case "--fullscreen":
                        options.Fullscreen = true;
                        break;
                    case "--relocated":
                        options.Relocated = true;
                        break;
                }
            }

            return options;
        }

        public string ToArguments()
        {
            var args = new List<string>();
            if (Install)
            {
                args.Add("--install");
            }

            if (!string.IsNullOrEmpty(Version))
            {
                args.Add("--version");
                args.Add(Version);
            }

            if (WaitPid > 0)
            {
                args.Add("--wait-pid");
                args.Add(WaitPid.ToString());
            }

            if (Fullscreen)
            {
                args.Add("--fullscreen");
            }

            if (Relocated)
            {
                args.Add("--relocated");
            }

            return string.Join(" ", args);
        }
    }

    public partial class App : Application
    {
        public static string WorkDirectory => Path.Combine(Path.GetTempPath(), "GameroomUpdater");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var options = UpdaterOptions.Parse(e.Args);
            if (options.Install && !options.Relocated && TryRelocate(options))
            {
                Shutdown();
                return;
            }

            new MainWindow(options).Show();
        }

        /// <summary>
        /// The installer replaces files in Gameroom's folder, including this exe,
        /// so updates run from a copy in the temporary folder.
        /// </summary>
        internal static bool TryRelocate(UpdaterOptions options)
        {
            try
            {
                Directory.CreateDirectory(WorkDirectory);
                var target = Path.Combine(WorkDirectory, "GameroomUpdater.exe");
                var current = Process.GetCurrentProcess().MainModule.FileName;
                if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                File.Copy(current, target, true);
                options.Relocated = true;
                Process.Start(target, options.ToArguments());
                return true;
            }
            catch
            {
                // Another updater may be running from the temporary folder, keep going from here.
                return false;
            }
        }
    }
}
