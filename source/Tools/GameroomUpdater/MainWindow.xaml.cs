using Playnite.Gameroom.Updates;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Gameroom.Updater
{
    public partial class MainWindow : Window
    {
        private readonly UpdaterOptions options;
        private readonly GameroomReleaseClient client = new GameroomReleaseClient();
        private readonly CancellationTokenSource cancelToken = new CancellationTokenSource();
        private GameroomInstallation installation;
        private ReleaseInfo release;
        private TaskCompletionSource<bool> retryRequested;
        private bool installerRunning;
        private DateTime lastProgressUpdate;

        internal MainWindow(UpdaterOptions options)
        {
            this.options = options;
            InitializeComponent();
            Title = Strings.Title;
            ButtonClose.Content = Strings.Close;
            TextNotesHeader.Text = Strings.WhatsNew;
            Loaded += async (_, __) => await StartAsync();
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            // Stopping the installer half way would leave a broken installation.
            if (installerRunning)
            {
                e.Cancel = true;
                return;
            }

            cancelToken.Cancel();
            retryRequested?.TrySetResult(false);
        }

        private async Task StartAsync()
        {
            installation = GameroomInstallation.Find();
            if (options.Install)
            {
                await InstallAsync();
            }
            else
            {
                await CheckAsync();
            }
        }

        private void SetStatus(string text)
        {
            TextStatus.Text = text;
        }

        private void ShowPrimary(string text)
        {
            ButtonPrimary.Content = text;
            ButtonPrimary.Visibility = Visibility.Visible;
            ButtonPrimary.IsEnabled = true;
        }

        private void ShowNotes(IEnumerable<ReleaseInfo> releases)
        {
            var notes = string.Join(Environment.NewLine + Environment.NewLine,
                releases.Where(a => !string.IsNullOrWhiteSpace(a.Notes)).Select(a => $"{a.VersionString}{Environment.NewLine}{a.Notes.Trim()}"));
            if (notes.Length > 0)
            {
                TextNotes.Text = notes;
                PanelNotes.Visibility = Visibility.Visible;
            }
        }

        private async Task<List<ReleaseInfo>> GetReleasesAsync()
        {
            try
            {
                return await Task.Run(() => client.GetReleases());
            }
            catch (Exception e)
            {
                SetStatus(string.Format(Strings.ConnectionFailed, e.Message));
                return null;
            }
        }

        private async Task CheckAsync()
        {
            SetStatus(Strings.Checking);
            var releases = await GetReleasesAsync();
            if (releases == null)
            {
                ShowPrimary(Strings.Retry);
                return;
            }

            release = releases.FirstOrDefault();
            if (release == null)
            {
                SetStatus(Strings.NoReleases);
                return;
            }

            if (installation == null)
            {
                SetStatus(string.Format(Strings.NotInstalled, release.VersionString));
                ShowNotes(new[] { release });
                ShowPrimary(Strings.Install);
                return;
            }

            if (release.Version <= installation.Version)
            {
                SetStatus(string.Format(Strings.UpToDate, installation.Version.ToString(3)));
                return;
            }

            SetStatus(string.Format(Strings.UpdateAvailable, release.VersionString, installation.Version.ToString(3)));
            ShowNotes(releases.Where(a => a.Version > installation.Version));
            ShowPrimary(Strings.Update);
        }

        private async void ButtonPrimary_Click(object sender, RoutedEventArgs e)
        {
            ButtonPrimary.IsEnabled = false;
            if (retryRequested != null)
            {
                retryRequested.TrySetResult(true);
                return;
            }

            if (release == null)
            {
                ButtonPrimary.Visibility = Visibility.Collapsed;
                await CheckAsync();
                return;
            }

            if (installation != null)
            {
                options.Install = true;
                options.Version = release.VersionString;
                if (App.TryRelocate(options))
                {
                    Close();
                    return;
                }
            }

            await InstallAsync();
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async Task InstallAsync()
        {
            ButtonPrimary.Visibility = Visibility.Collapsed;
            if (release == null || (options.Version != null && release.VersionString != options.Version))
            {
                SetStatus(Strings.Checking);
                var releases = await GetReleasesAsync();
                if (releases == null)
                {
                    return;
                }

                release = releases.FirstOrDefault(a => a.VersionString == options.Version) ?? releases.FirstOrDefault();
                if (release == null)
                {
                    SetStatus(Strings.NoReleases);
                    return;
                }
            }

            string setupPath;
            try
            {
                Progress.Visibility = Visibility.Visible;
                var downloadDir = Path.Combine(App.WorkDirectory, release.VersionString);
                setupPath = await Task.Run(() => client.Download(release, downloadDir, OnDownloadProgress, cancelToken.Token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Progress.Visibility = Visibility.Hidden;
                SetStatus(string.Format(Strings.Failed, e.Message));
                return;
            }

            if (installation == null)
            {
                // First installation: the regular setup wizard lets the user pick options.
                Process.Start(setupPath);
                Close();
                return;
            }

            if (!await WaitForGameroomExitAsync())
            {
                return;
            }

            SetStatus(string.Format(Strings.Installing, release.VersionString));
            Progress.IsIndeterminate = true;
            var logPath = Path.Combine(App.WorkDirectory, $"setup-{release.VersionString}.log");
            int exitCode;
            installerRunning = true;
            ButtonClose.IsEnabled = false;
            try
            {
                exitCode = await Task.Run(() =>
                {
                    var args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /DIR=\"{installation.Directory}\" /LOG=\"{logPath}\"";
                    using (var process = Process.Start(setupPath, args))
                    {
                        process.WaitForExit();
                        return process.ExitCode;
                    }
                });
            }
            catch (Exception e)
            {
                exitCode = -1;
                SetStatus(string.Format(Strings.Failed, e.Message));
            }
            finally
            {
                installerRunning = false;
                ButtonClose.IsEnabled = true;
                Progress.IsIndeterminate = false;
            }

            if (exitCode != 0)
            {
                Progress.Visibility = Visibility.Hidden;
                if (exitCode != -1)
                {
                    SetStatus(string.Format(Strings.SetupFailed, exitCode, logPath));
                }

                return;
            }

            Progress.Value = 100;
            SetStatus(string.Format(Strings.Updated, release.VersionString));
            var updated = GameroomInstallation.Find() ?? installation;
            try
            {
                Process.Start(options.Fullscreen ? updated.FullscreenExecutable : updated.DesktopExecutable);
            }
            catch
            {
            }

            await Task.Delay(2500);
            Close();
        }

        private void OnDownloadProgress(ReleaseDownloadProgress progress)
        {
            if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds < 100 && progress.DownloadedBytes < progress.TotalBytes)
            {
                return;
            }

            lastProgressUpdate = DateTime.Now;
            var percent = progress.Percent;
            var downloaded = progress.DownloadedBytes / 1024.0 / 1024.0;
            var total = progress.TotalBytes / 1024.0 / 1024.0;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Progress.Value = percent;
                SetStatus(string.Format(Strings.Downloading, release.VersionString, downloaded, total));
            }));
        }

        /// <summary>
        /// Files of a running Gameroom can't be replaced. Games keep running, they don't depend on Gameroom.
        /// </summary>
        private async Task<bool> WaitForGameroomExitAsync()
        {
            SetStatus(Strings.WaitingForGameroom);
            if (options.WaitPid > 0)
            {
                await Task.Run(() =>
                {
                    try
                    {
                        using (var process = Process.GetProcessById(options.WaitPid))
                        {
                            process.WaitForExit(60000);
                        }
                    }
                    catch
                    {
                        // Already closed.
                    }
                });
            }

            while (true)
            {
                var running = GameroomInstallation.GetRunningProcesses();
                if (running.Count == 0)
                {
                    return true;
                }

                running.ForEach(a => a.Dispose());
                SetStatus(Strings.CloseGameroom);
                retryRequested = new TaskCompletionSource<bool>();
                ShowPrimary(Strings.Retry);
                var retry = await retryRequested.Task;
                retryRequested = null;
                ButtonPrimary.Visibility = Visibility.Collapsed;
                if (!retry)
                {
                    return false;
                }
            }
        }
    }
}
