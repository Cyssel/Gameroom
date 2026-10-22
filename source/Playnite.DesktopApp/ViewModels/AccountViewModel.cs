using Playnite.Commands;
using Playnite.Gameroom;
using Playnite.Gameroom.Cloud;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Playnite.DesktopApp.ViewModels
{
    public class CloudGameGroup
    {
        public string GameName { get; set; }
        public string Details { get; set; }
        public string LastBackup { get; set; }
    }

    /// <summary>
    /// Gameroom account page: sign in or create an account, profile and cloud saves.
    /// </summary>
    public class AccountViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly DesktopAppViewModel model;
        private GameroomService subscribedService;

        public AccountViewModel(DesktopAppViewModel model)
        {
            this.model = model;
            SignInCommand = new RelayCommand<object>(a => SignIn(a as PasswordBox), _ => !IsBusy);
            SignUpCommand = new RelayCommand<object>(a => SignUp(a as PasswordBox), _ => !IsBusy);
            SignOutCommand = new RelayCommand(SignOut);
            SyncCommand = new RelayCommand(Sync, () => !IsSyncing);
            ConnectServerCommand = new RelayCommand(ConnectServer, () => !IsBusy);
            ChangeServerCommand = new RelayCommand(ChangeServer);
            CancelServerCommand = new RelayCommand(() => IsEditingServer = false);
            OpenServerGuideCommand = new RelayCommand(() => GlobalCommands.NavigateUrl(ServerGuideUrl));
        }

        public const string ServerGuideUrl = "https://github.com/" + Gameroom.Updates.ReleaseChannel.GitHubRepository + "/blob/main/docs/SUPABASE.md";

        public RelayCommand<object> SignInCommand { get; }
        public RelayCommand<object> SignUpCommand { get; }
        public RelayCommand SignOutCommand { get; }
        public RelayCommand SyncCommand { get; }
        public RelayCommand ConnectServerCommand { get; }
        public RelayCommand ChangeServerCommand { get; }
        public RelayCommand CancelServerCommand { get; }
        public RelayCommand OpenServerGuideCommand { get; }

        private GameroomService Service => GameroomService.Instance;

        private bool isSignedIn;
        public bool IsSignedIn
        {
            get => isSignedIn;
            private set
            {
                SetValue(ref isSignedIn, value);
                OnPropertyChanged(nameof(ShowServerSetup));
                OnPropertyChanged(nameof(ShowSignIn));
            }
        }

        private bool isServerConfigured;
        /// <summary>The user connected their own Supabase project, accounts and the cloud need one.</summary>
        public bool IsServerConfigured
        {
            get => isServerConfigured;
            private set
            {
                SetValue(ref isServerConfigured, value);
                OnPropertyChanged(nameof(ShowServerSetup));
                OnPropertyChanged(nameof(ShowSignIn));
            }
        }

        private bool isEditingServer;
        public bool IsEditingServer
        {
            get => isEditingServer;
            set
            {
                SetValue(ref isEditingServer, value);
                StatusMessage = null;
                OnPropertyChanged(nameof(ShowServerSetup));
                OnPropertyChanged(nameof(ShowSignIn));
            }
        }

        public bool ShowServerSetup => !IsSignedIn && (!IsServerConfigured || IsEditingServer);
        public bool ShowSignIn => !IsSignedIn && IsServerConfigured && !IsEditingServer;

        private string serverUrl;
        public string ServerUrl
        {
            get => serverUrl;
            set => SetValue(ref serverUrl, value);
        }

        private string serverKey;
        public string ServerKey
        {
            get => serverKey;
            set => SetValue(ref serverKey, value);
        }

        private string serverName;
        /// <summary>Host of the connected project, shown under the sign in form.</summary>
        public string ServerName
        {
            get => serverName;
            private set => SetValue(ref serverName, value);
        }

        private bool isSignUpMode;
        public bool IsSignUpMode
        {
            get => isSignUpMode;
            set
            {
                SetValue(ref isSignUpMode, value);
                OnPropertyChanged(nameof(IsSignInMode));
                StatusMessage = null;
            }
        }

        public bool IsSignInMode
        {
            get => !isSignUpMode;
            set => IsSignUpMode = !value;
        }

        private bool isBusy;
        public bool IsBusy
        {
            get => isBusy;
            private set => SetValue(ref isBusy, value);
        }

        private string email;
        public string Email
        {
            get => email;
            set => SetValue(ref email, value);
        }

        private string username;
        public string Username
        {
            get => username;
            set => SetValue(ref username, value);
        }

        private string statusMessage;
        public string StatusMessage
        {
            get => statusMessage;
            private set => SetValue(ref statusMessage, value);
        }

        private bool statusIsError;
        public bool StatusIsError
        {
            get => statusIsError;
            private set => SetValue(ref statusIsError, value);
        }

        private string profileName;
        public string ProfileName
        {
            get => profileName;
            private set
            {
                SetValue(ref profileName, value);
                OnPropertyChanged(nameof(ProfileInitial));
            }
        }

        public string ProfileInitial => ProfileName.IsNullOrEmpty() ? "?" : ProfileName.Substring(0, 1).ToUpperInvariant();

        private string profileEmail;
        public string ProfileEmail
        {
            get => profileEmail;
            private set => SetValue(ref profileEmail, value);
        }

        private string memberSince;
        public string MemberSince
        {
            get => memberSince;
            private set => SetValue(ref memberSince, value);
        }

        private string totalPlaytime;
        public string TotalPlaytime
        {
            get => totalPlaytime;
            private set => SetValue(ref totalPlaytime, value);
        }

        private string playedGames;
        public string PlayedGames
        {
            get => playedGames;
            private set => SetValue(ref playedGames, value);
        }

        private string cloudSummary;
        public string CloudSummary
        {
            get => cloudSummary;
            private set => SetValue(ref cloudSummary, value);
        }

        private string syncStatus;
        public string SyncStatus
        {
            get => syncStatus;
            private set => SetValue(ref syncStatus, value);
        }

        private bool isSyncing;
        public bool IsSyncing
        {
            get => isSyncing;
            private set => SetValue(ref isSyncing, value);
        }

        private List<CloudGameGroup> cloudGames = new List<CloudGameGroup>();
        public List<CloudGameGroup> CloudGames
        {
            get => cloudGames;
            private set
            {
                SetValue(ref cloudGames, value);
                OnPropertyChanged(nameof(HasCloudGames));
            }
        }

        public bool HasCloudGames => CloudGames.Count > 0;

        public bool CloudSavesEnabled
        {
            get => model.AppSettings.Gameroom.CloudSavesEnabled;
            set
            {
                model.AppSettings.Gameroom.CloudSavesEnabled = value;
                model.AppSettings.SaveSettings();
                OnPropertyChanged();
                if (value)
                {
                    Sync();
                }
            }
        }

        private void Subscribe()
        {
            var service = Service;
            if (service == null || subscribedService == service)
            {
                return;
            }

            subscribedService = service;
            service.Cloud.SessionChanged += (_, __) => OnUiThread(Refresh);
            service.CloudSync.StatusChanged += (_, __) => OnUiThread(UpdateSyncStatus);
            service.CloudSync.SyncCompleted += (_, __) => OnUiThread(() => _ = LoadCloudDataAsync());
        }

        private static void OnUiThread(Action action)
        {
            Application.Current?.Dispatcher.BeginInvoke(action);
        }

        public void Refresh()
        {
            Subscribe();
            var service = Service;
            IsServerConfigured = service?.Cloud.IsConfigured == true;
            ServerName = IsServerConfigured && Uri.TryCreate(service.Cloud.ServerUrl, UriKind.Absolute, out var server) ? server.Host : null;
            IsSignedIn = service?.Cloud.IsSignedIn == true;
            if (!IsSignedIn)
            {
                return;
            }

            ProfileEmail = service.Cloud.Email;
            ProfileName = ProfileName ?? service.Cloud.Email?.Split('@')[0];
            var games = model.Database.Games.ToList();
            TotalPlaytime = GameroomService.FormatDuration((ulong)games.Sum(a => (long)a.Playtime));
            PlayedGames = games.Count(a => a.Playtime > 0).ToString();
            UpdateSyncStatus();
            _ = LoadCloudDataAsync();
        }

        private void UpdateSyncStatus()
        {
            var sync = Service?.CloudSync;
            if (sync == null)
            {
                return;
            }

            IsSyncing = sync.IsSyncing;
            if (sync.IsSyncing)
            {
                SyncStatus = ResourceProvider.GetString("LOCGameroomCloudSyncing");
            }
            else if (!sync.LastError.IsNullOrEmpty())
            {
                SyncStatus = string.Format(ResourceProvider.GetString("LOCGameroomCloudSyncFailed"), sync.LastError);
            }
            else if (sync.LastSync != null)
            {
                SyncStatus = string.Format(ResourceProvider.GetString("LOCGameroomCloudLastSync"), sync.LastSync.Value.ToString("t", CultureInfo.CurrentCulture));
            }
            else
            {
                SyncStatus = ResourceProvider.GetString("LOCGameroomCloudNotSynced");
            }

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private async Task LoadCloudDataAsync()
        {
            var service = Service;
            if (service?.Cloud.IsSignedIn != true)
            {
                return;
            }

            try
            {
                var profile = await service.Cloud.GetProfileAsync();
                if (profile != null)
                {
                    ProfileName = profile.Username;
                    MemberSince = string.Format(ResourceProvider.GetString("LOCGameroomAccountMemberSince"),
                        profile.CreatedAt.ToLocalTime().ToString("MMMM yyyy", GameroomLocalization.Culture));
                }

                var saves = await service.Cloud.GetSavesAsync();
                var totalSize = saves.Sum(a => a.Size);
                CloudSummary = string.Format(ResourceProvider.GetString("LOCGameroomAccountCloudSummary"),
                    HomeViewModel.FormatCount(saves.Count, "LOCGameroomCountBackups"),
                    HomeViewModel.FormatCount(saves.Select(a => a.GameKey).Distinct().Count(), "LOCGameroomCountGames"),
                    FormatSize(totalSize));
                CloudGames = saves.GroupBy(a => a.GameKey)
                    .Select(g => new
                    {
                        Latest = g.OrderByDescending(a => a.CreatedAt).First(),
                        Count = g.Count(),
                        Size = g.Sum(a => a.Size)
                    })
                    .OrderByDescending(a => a.Latest.CreatedAt)
                    .Select(a => new CloudGameGroup
                    {
                        GameName = a.Latest.GameName,
                        Details = $"{HomeViewModel.FormatCount(a.Count, "LOCGameroomCountBackups")}  ·  {FormatSize(a.Size)}",
                        LastBackup = a.Latest.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                    })
                    .ToList();
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to load account data.");
                SyncStatus = GameroomCloud.DescribeError(e);
            }
        }

        private static string FormatSize(long bytes)
        {
            return bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
        }

        private bool ValidateInput(PasswordBox passwordBox, bool signUp)
        {
            if (Email.IsNullOrWhiteSpace() || !Email.Contains("@"))
            {
                ShowStatus(ResourceProvider.GetString("LOCGameroomCloudErrorInvalidEmail"), true);
                return false;
            }

            if (passwordBox == null || passwordBox.Password.Length < 6)
            {
                ShowStatus(ResourceProvider.GetString("LOCGameroomCloudErrorWeakPassword"), true);
                return false;
            }

            if (signUp && (Username.IsNullOrWhiteSpace() || Username.Trim().Length < 2))
            {
                ShowStatus(ResourceProvider.GetString("LOCGameroomCloudErrorUsername"), true);
                return false;
            }

            return true;
        }

        private void ShowStatus(string message, bool isError)
        {
            StatusIsError = isError;
            StatusMessage = message;
        }

        private async void SignIn(PasswordBox passwordBox)
        {
            if (Service == null || !ValidateInput(passwordBox, false))
            {
                return;
            }

            IsBusy = true;
            ShowStatus(ResourceProvider.GetString("LOCGameroomCloudSigningIn"), false);
            var result = await Service.Cloud.SignInAsync(Email, passwordBox.Password);
            IsBusy = false;
            if (result.Success)
            {
                passwordBox.Clear();
                StatusMessage = null;
                Refresh();
            }
            else
            {
                ShowStatus(result.Error, true);
            }
        }

        private async void SignUp(PasswordBox passwordBox)
        {
            if (Service == null || !ValidateInput(passwordBox, true))
            {
                return;
            }

            IsBusy = true;
            ShowStatus(ResourceProvider.GetString("LOCGameroomCloudCreatingAccount"), false);
            var result = await Service.Cloud.SignUpAsync(Email, passwordBox.Password, Username);
            IsBusy = false;
            if (!result.Success)
            {
                ShowStatus(result.Error, true);
                return;
            }

            passwordBox.Clear();
            if (result.NeedsConfirmation)
            {
                IsSignUpMode = false;
                ShowStatus(ResourceProvider.GetString("LOCGameroomCloudConfirmEmail"), false);
            }
            else
            {
                StatusMessage = null;
                Refresh();
            }
        }

        private async void SignOut()
        {
            if (Service == null)
            {
                return;
            }

            await Service.Cloud.SignOutAsync();
            ProfileName = null;
            CloudGames = new List<CloudGameGroup>();
            Refresh();
        }

        private void ChangeServer()
        {
            ServerUrl = Service?.Cloud.ServerUrl;
            ServerKey = null;
            IsEditingServer = true;
        }

        private async void ConnectServer()
        {
            if (Service == null)
            {
                return;
            }

            IsBusy = true;
            ShowStatus(ResourceProvider.GetString("LOCGameroomCloudServerChecking"), false);
            var result = await GameroomCloud.TestServerAsync(ServerUrl, ServerKey);
            IsBusy = false;
            if (!result.Success)
            {
                ShowStatus(result.Error, true);
                return;
            }

            Service.ConfigureCloudServer(ServerUrl, ServerKey);
            ServerKey = null;
            isEditingServer = false;
            Refresh();
            IsSignUpMode = true;
            ShowStatus(ResourceProvider.GetString("LOCGameroomCloudServerConnected"), false);
        }

        private async void Sync()
        {
            if (Service?.Cloud.IsSignedIn != true)
            {
                return;
            }

            await Task.Run(Service.CloudSync.SyncAsync);
            await LoadCloudDataAsync();
        }
    }
}
