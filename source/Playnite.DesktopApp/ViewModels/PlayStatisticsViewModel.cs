using Playnite.DesktopApp.Windows;
using Playnite.Gameroom;
using Playnite.SDK;
using Playnite.Windows;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace Playnite.DesktopApp.ViewModels
{
    public class PlayStatisticsBar
    {
        /// <summary>Height in pixels inside the chart area.</summary>
        public double Height { get; set; }
        /// <summary>Axis label, empty for bars without one to avoid overlapping labels.</summary>
        public string Label { get; set; }
        public string ToolTip { get; set; }
    }

    public class PlayStatisticsGameItem
    {
        public int Rank { get; set; }
        public string Name { get; set; }
        public string DurationText { get; set; }
        public string PercentageText { get; set; }
        public string SessionsText { get; set; }
        /// <summary>Bar length relative to the most played game.</summary>
        public GridLength BarWidth { get; set; }
        public GridLength RestWidth { get; set; }
        public RelayCommand SelectCommand { get; set; }
    }

    /// <summary>
    /// Global play time statistics built from the session history Gameroom records.
    /// </summary>
    public class PlayStatisticsViewModel : ObservableObject
    {
        public const double ChartHeight = 200;
        private const int maxAxisLabels = 12;
        private static readonly double[] axisSteps =
        {
            5 * 60, 10 * 60, 15 * 60, 30 * 60,
            3600, 2 * 3600, 3 * 3600, 5 * 3600, 10 * 3600, 20 * 3600, 25 * 3600, 50 * 3600,
            100 * 3600, 200 * 3600, 250 * 3600, 500 * 3600, 1000 * 3600, 2000 * 3600, 5000 * 3600
        };

        private readonly IWindowFactory window;
        private readonly List<GameSession> sessions;
        private readonly Func<Guid, string> getLibraryGameName;
        private readonly Action<Guid> selectGame;

        public static RelayCommand OpenCommand { get; } = new RelayCommand(() => Open(), () => GameroomService.Instance != null);

        private PlayStatisticsPeriod period = PlayStatisticsPeriod.Last30Days;
        public PlayStatisticsPeriod Period
        {
            get => period;
            set
            {
                period = value;
                OnPropertyChanged();
                Calculate();
            }
        }

        public bool HasData { get; private set; }
        public string RangeText { get; private set; }
        public string TotalText { get; private set; }
        public string SessionsText { get; private set; }
        public string AverageSessionText { get; private set; }
        public string DaysPlayedText { get; private set; }
        public string GamesText { get; private set; }
        public string ChartTitle { get; private set; }
        /// <summary>Value of each horizontal grid line, top to bottom, without the zero line.</summary>
        public List<string> AxisLabels { get; private set; }
        public List<PlayStatisticsBar> Bars { get; private set; }
        public List<PlayStatisticsGameItem> TopGames { get; private set; }

        public RelayCommand CloseCommand => new RelayCommand(() => window.Close());

        public PlayStatisticsViewModel(
            IWindowFactory window,
            IEnumerable<GameSession> sessions,
            Func<Guid, string> getLibraryGameName,
            Action<Guid> selectGame)
        {
            this.window = window;
            this.sessions = sessions.ToList();
            this.getLibraryGameName = getLibraryGameName;
            this.selectGame = selectGame;
            Calculate();
        }

        public static void Open()
        {
            var service = GameroomService.Instance;
            var mainModel = DesktopApplication.Current?.MainModel;
            if (service == null || mainModel == null)
            {
                return;
            }

            new PlayStatisticsViewModel(
                new PlayStatisticsWindowFactory(),
                service.Database.GetAllSessions(),
                id => mainModel.Database.Games.Get(id)?.Name,
                id =>
                {
                    mainModel.SwitchToLibraryView();
                    mainModel.SelectGame(id);
                }).OpenView();
        }

        public void OpenView()
        {
            window.CreateAndOpenDialog(this);
        }

        private void Calculate()
        {
            var culture = CultureInfo.CurrentCulture;
            var result = PlayStatistics.Calculate(sessions, Period, DateTime.Now, culture.DateTimeFormat.FirstDayOfWeek);

            HasData = result.TotalSeconds > 0;
            RangeText = $"{result.From.ToString("d", culture)} – {result.To.AddDays(-1).ToString("d", culture)}";
            TotalText = FormatDuration(result.TotalSeconds);
            SessionsText = result.SessionCount.ToString(culture);
            AverageSessionText = result.SessionCount > 0 ? FormatDuration(result.TotalSeconds / result.SessionCount) : "-";
            DaysPlayedText = string.Format(
                ResourceProvider.GetString("LOCGameroomPlayStatsDaysPlayedValue"),
                result.DaysPlayed,
                (int)Math.Round((result.To - result.From).TotalDays));
            GamesText = result.GameCount.ToString(culture);
            ChartTitle = ResourceProvider.GetString("LOCGameroomPlayStatsPer" + result.Grouping);

            var maxSeconds = result.Buckets.Count > 0 ? result.Buckets.Max(a => a.Seconds) : 0;
            var step = axisSteps.FirstOrDefault(a => maxSeconds / a <= 4);
            if (step == 0)
            {
                step = Math.Ceiling(maxSeconds / 4 / 3600) * 3600;
            }

            var lines = Math.Max(1, (int)Math.Ceiling(maxSeconds / step));
            var axisMax = lines * step;
            AxisLabels = Enumerable.Range(1, lines).Reverse().Select(a => FormatAxisValue(a * step)).ToList();

            var labelStep = (int)Math.Ceiling(result.Buckets.Count / (double)maxAxisLabels);
            var multipleYears = result.From.Year != result.To.AddDays(-1).Year;
            Bars = result.Buckets.Select((bucket, index) => new PlayStatisticsBar
            {
                Height = bucket.Seconds / axisMax * ChartHeight,
                // Label the most recent bar and every n-th one before it.
                Label = (result.Buckets.Count - 1 - index) % labelStep == 0
                    ? GetBarLabel(bucket, result.Grouping, result.Buckets.Count, multipleYears, GameroomLocalization.Culture)
                    : string.Empty,
                ToolTip = $"{GetBarTitle(bucket, result.Grouping, GameroomLocalization.Culture)}: {FormatDuration(bucket.Seconds)}"
            }).ToList();

            var topSeconds = result.TopGames.Count > 0 ? result.TopGames[0].Seconds : 0;
            TopGames = result.TopGames.Select((game, index) =>
            {
                var libraryName = getLibraryGameName(game.GameId);
                var share = game.Seconds / topSeconds;
                return new PlayStatisticsGameItem
                {
                    Rank = index + 1,
                    Name = libraryName ?? game.GameName ?? "?",
                    DurationText = FormatDuration(game.Seconds),
                    PercentageText = game.Percentage.ToString(game.Percentage < 10 ? "0.#" : "0", culture) + "%",
                    SessionsText = game.SessionCount == 1
                        ? ResourceProvider.GetString("LOCGameroomPlayStatsGameSession")
                        : string.Format(ResourceProvider.GetString("LOCGameroomPlayStatsGameSessions"), game.SessionCount),
                    BarWidth = new GridLength(share, GridUnitType.Star),
                    RestWidth = new GridLength(1 - share, GridUnitType.Star),
                    SelectCommand = new RelayCommand(() =>
                    {
                        window.Close();
                        selectGame(game.GameId);
                    }, () => libraryName != null)
                };
            }).ToList();

            // Refresh all bindings, every value depends on the period.
            OnPropertyChanged(string.Empty);
        }

        private static string FormatDuration(double seconds)
        {
            return GameroomService.FormatDuration((ulong)Math.Round(seconds));
        }

        private static string FormatAxisValue(double seconds)
        {
            return seconds % 3600 == 0 ? $"{seconds / 3600} h" : $"{seconds / 60} min";
        }

        private static string GetBarLabel(PlayTimeBucket bucket, PlayStatisticsGrouping grouping, int barCount, bool multipleYears, CultureInfo culture)
        {
            switch (grouping)
            {
                case PlayStatisticsGrouping.Day:
                    return bucket.Start.ToString(barCount <= 7 ? "ddd d" : "d MMM", culture);
                case PlayStatisticsGrouping.Week:
                    return bucket.Start.ToString("d MMM", culture);
                default:
                    return bucket.Start.ToString(multipleYears ? "MMM yy" : "MMM", culture);
            }
        }

        private static string GetBarTitle(PlayTimeBucket bucket, PlayStatisticsGrouping grouping, CultureInfo culture)
        {
            switch (grouping)
            {
                case PlayStatisticsGrouping.Day:
                    return bucket.Start.ToString("D", culture);
                case PlayStatisticsGrouping.Week:
                    return $"{bucket.Start.ToString("d", culture)} – {bucket.End.AddDays(-1).ToString("d", culture)}";
                default:
                    return bucket.Start.ToString("Y", culture);
            }
        }
    }
}
