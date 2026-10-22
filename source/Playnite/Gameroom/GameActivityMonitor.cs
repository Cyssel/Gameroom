using Playnite.Common;
using Playnite.Database;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Gameroom
{
    public class TrackedSession
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public DateTime Start { get; set; }
        public DateTime LastSeen { get; set; }
        public string Executable { get; set; }
        public int ProcessId { get; set; }
        public DateTime ProcessStart { get; set; }
        /// <summary>Session lasted long enough to be shown as running.</summary>
        public bool Confirmed { get; set; }
        public DateTime LastPersisted { get; set; }
        /// <summary>Part of the session already added to the game's playtime while it was running.</summary>
        public ulong CreditedSeconds { get; set; }

        public ulong Duration => (ulong)Math.Max(0, Math.Round((LastSeen - Start).TotalSeconds));
    }

    public class UnknownGameSession
    {
        public UnknownGameInfo Info { get; set; }
        public DateTime Start { get; set; }
        public DateTime LastSeen { get; set; }
        public bool Announced { get; set; }
    }

    /// <summary>
    /// Watches running processes and records play sessions of library games started outside of Gameroom
    /// (desktop shortcuts, store clients, directly from the game folder...). Games started from Gameroom
    /// are left to the regular play controllers.
    /// </summary>
    public class GameActivityMonitor : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // How long a game must run before it's shown as running (filters launcher stubs that restart through a store client).
        private const int confirmSeconds = 10;
        // Processes of a game may disappear for a while, e.g. when a launcher restarts the game.
        private const int exitGraceSeconds = 15;
        // Unknown games are announced only after running for a while.
        private const int unknownAnnounceSeconds = 30;
        private const int persistIntervalSeconds = 60;
        // Playtime of running games is updated this often, so it grows while playing.
        private const int creditIntervalSeconds = 60;

        private readonly IGameDatabaseMain database;
        private readonly GameroomDatabase gameroomDb;
        private readonly Func<GameroomSettings> getSettings;
        private readonly Func<List<ProcessSnapshot>> processSource;
        private readonly Func<int, DateTime, bool> isProcessRunning;
        private readonly UnknownGameDetector unknownDetector = new UnknownGameDetector();
        private readonly object stateLock = new object();
        private readonly Dictionary<Guid, TrackedSession> tracked = new Dictionary<Guid, TrackedSession>();
        private readonly Dictionary<string, UnknownGameSession> unknown = new Dictionary<string, UnknownGameSession>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, DateTime> suppressedUntil = new Dictionary<Guid, DateTime>();
        private CancellationTokenSource cancelToken;
        private Task loopTask;
        private GameMatcher matcher;
        private volatile bool matcherDirty = true;

        public event EventHandler<TrackedSession> SessionStarted;
        public event EventHandler<GameSession> SessionEnded;
        public event EventHandler<UnknownGameSession> UnknownGameDetected;

        public bool IsRunning => loopTask != null && !loopTask.IsCompleted;

        // Settings object is replaced when settings are edited, so it's always read fresh.
        private GameroomSettings settings => getSettings();

        public GameActivityMonitor(IGameDatabaseMain database, GameroomDatabase gameroomDb, Func<GameroomSettings> getSettings)
            : this(database, gameroomDb, getSettings, new ProcessScanner().Scan, ProcessScanner.IsProcessRunning)
        {
        }

        internal GameActivityMonitor(
            IGameDatabaseMain database,
            GameroomDatabase gameroomDb,
            Func<GameroomSettings> getSettings,
            Func<List<ProcessSnapshot>> processSource,
            Func<int, DateTime, bool> isProcessRunning)
        {
            this.database = database;
            this.gameroomDb = gameroomDb;
            this.getSettings = getSettings;
            this.processSource = processSource;
            this.isProcessRunning = isProcessRunning;
            database.Games.ItemCollectionChanged += Games_ItemCollectionChanged;
            database.Games.ItemUpdated += Games_ItemUpdated;
        }

        public void Dispose()
        {
            Stop();
            database.Games.ItemCollectionChanged -= Games_ItemCollectionChanged;
            database.Games.ItemUpdated -= Games_ItemUpdated;
        }

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            matcherDirty = true;
        }

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            foreach (var update in e.UpdatedItems)
            {
                var oldGame = update.OldData;
                var newGame = update.NewData;
                if (oldGame.InstallDirectory != newGame.InstallDirectory ||
                    oldGame.IsInstalled != newGame.IsInstalled ||
                    !GameActionsEqual(oldGame, newGame))
                {
                    matcherDirty = true;
                    return;
                }
            }
        }

        private static bool GameActionsEqual(Game a, Game b)
        {
            var aPaths = a.GameActions?.Select(x => x.Path).ToList() ?? new List<string>();
            var bPaths = b.GameActions?.Select(x => x.Path).ToList() ?? new List<string>();
            return aPaths.SequenceEqual(bPaths);
        }

        public void InvalidateLibrary()
        {
            matcherDirty = true;
        }

        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            RecoverActiveSessions();
            cancelToken = new CancellationTokenSource();
            var token = cancelToken.Token;
            loopTask = Task.Run(async () =>
            {
                logger.Info("Live game detection started.");
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (settings.LiveDetectionEnabled)
                        {
                            Tick(DateTime.Now);
                        }
                        else
                        {
                            EndAllSessions();
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Live game detection tick failed.");
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(settings.DetectionInterval), token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            });
        }

        /// <summary>
        /// Stops monitoring. Running sessions are kept in the database and continued on next start
        /// if the game is still running by then.
        /// </summary>
        public void Stop()
        {
            if (cancelToken != null)
            {
                cancelToken.Cancel();
                try
                {
                    loopTask?.Wait(5000);
                }
                catch
                {
                }

                cancelToken.Dispose();
                cancelToken = null;
                loopTask = null;
                logger.Info("Live game detection stopped.");
            }

            lock (stateLock)
            {
                var now = DateTime.Now;
                foreach (var session in tracked.Values.Where(a => a.Confirmed))
                {
                    session.LastSeen = now;
                    PersistSession(session);
                }

                tracked.Clear();
            }
        }

        /// <summary>
        /// Called when a session of a game started from Gameroom ends, so processes left behind
        /// aren't counted again from their original start time.
        /// </summary>
        public void NotifyLaunchedSessionEnded(Guid gameId)
        {
            lock (stateLock)
            {
                suppressedUntil[gameId] = DateTime.Now;
            }
        }

        public List<TrackedSession> GetTrackedSessions()
        {
            lock (stateLock)
            {
                return tracked.Values.Where(a => a.Confirmed).ToList();
            }
        }

        /// <summary>
        /// Continues tracking of an unknown game session under a game that was just added to the library,
        /// so the time played before adding it isn't lost.
        /// </summary>
        public void AdoptUnknownSession(string executable, Game game)
        {
            lock (stateLock)
            {
                matcherDirty = true;
                if (!unknown.TryGetValue(executable, out var session))
                {
                    return;
                }

                unknown.Remove(executable);
                if (!tracked.ContainsKey(game.Id))
                {
                    tracked[game.Id] = new TrackedSession
                    {
                        GameId = game.Id,
                        GameName = game.Name,
                        Start = session.Start,
                        LastSeen = session.LastSeen,
                        Executable = executable
                    };
                }
            }
        }

        public void IgnoreExecutable(string executable)
        {
            lock (stateLock)
            {
                if (!settings.IgnoredExecutables.Contains(executable, StringComparer.OrdinalIgnoreCase))
                {
                    settings.IgnoredExecutables.Add(executable);
                }

                unknown.Remove(executable);
                matcherDirty = true;
            }
        }

        private void RebuildMatcher()
        {
            matcherDirty = false;
            matcher = GameMatcher.FromLibrary(database.Games.ToList(), gameroomDb, settings.IgnoredExecutables.ToList());
            logger.Debug($"Live detection index rebuilt for {matcher.GameCount} games.");
        }

        internal void Tick(DateTime now)
        {
            if (matcher == null || matcherDirty)
            {
                RebuildMatcher();
            }

            var processes = processSource();
            var started = new List<TrackedSession>();
            var ended = new List<TrackedSession>();
            var credited = new List<TrackedSession>();
            var announced = new List<UnknownGameSession>();

            lock (stateLock)
            {
                var byGame = new Dictionary<Guid, List<ProcessSnapshot>>();
                var unmatched = new List<ProcessSnapshot>();
                foreach (var process in processes)
                {
                    var gameId = matcher.Match(process.Path);
                    if (gameId.HasValue)
                    {
                        if (!byGame.TryGetValue(gameId.Value, out var list))
                        {
                            list = new List<ProcessSnapshot>();
                            byGame.Add(gameId.Value, list);
                        }

                        list.Add(process);
                    }
                    else if (process.HasWindow)
                    {
                        unmatched.Add(process);
                    }
                }

                foreach (var pair in byGame)
                {
                    var game = database.Games.Get(pair.Key);
                    if (game == null)
                    {
                        matcherDirty = true;
                        continue;
                    }

                    tracked.TryGetValue(pair.Key, out var session);
                    if (session == null && (game.IsRunning || game.IsLaunching))
                    {
                        // Started from Gameroom, playtime is handled by its play controller.
                        suppressedUntil[pair.Key] = now;
                        continue;
                    }

                    var main = pair.Value.OrderBy(a => a.StartTime).First();
                    if (session == null)
                    {
                        var start = main.StartTime;
                        if (suppressedUntil.TryGetValue(pair.Key, out var suppressed) && suppressed > start)
                        {
                            start = suppressed;
                        }

                        if (start > now)
                        {
                            start = now;
                        }

                        session = new TrackedSession
                        {
                            GameId = game.Id,
                            GameName = game.Name,
                            Start = start
                        };

                        tracked.Add(game.Id, session);
                        logger.Info($"Detected {game.Name} running from {main.Path}");
                    }

                    session.LastSeen = now;
                    session.Executable = main.Path;
                    session.ProcessId = main.Id;
                    session.ProcessStart = main.StartTime;

                    if (!session.Confirmed && (now - session.Start).TotalSeconds >= confirmSeconds)
                    {
                        session.Confirmed = true;
                        started.Add(session);
                    }

                    if (session.Confirmed &&
                        session.Duration >= (ulong)settings.MinimumSessionLength &&
                        session.Duration >= session.CreditedSeconds + creditIntervalSeconds)
                    {
                        credited.Add(session);
                    }
                    else if (session.Confirmed && (now - session.LastPersisted).TotalSeconds >= persistIntervalSeconds)
                    {
                        PersistSession(session);
                    }
                }

                foreach (var session in tracked.Values.ToList())
                {
                    if (!byGame.ContainsKey(session.GameId) &&
                        (now - session.LastSeen).TotalSeconds >= Math.Max(exitGraceSeconds, settings.DetectionInterval * 2))
                    {
                        tracked.Remove(session.GameId);
                        ended.Add(session);
                    }
                }

                if (settings.DetectUnknownGames)
                {
                    foreach (var process in unmatched)
                    {
                        if (matcher.IsIgnored(process.Path))
                        {
                            continue;
                        }

                        var info = unknownDetector.Inspect(process);
                        if (info == null)
                        {
                            continue;
                        }

                        if (!unknown.TryGetValue(info.Executable, out var session))
                        {
                            session = new UnknownGameSession { Info = info, Start = process.StartTime > now ? now : process.StartTime };
                            unknown.Add(info.Executable, session);
                            logger.Info($"Unknown game candidate {info.SuggestedName} ({info.Engine}) at {info.Executable}");
                        }

                        session.LastSeen = now;
                        if (!session.Announced && (now - session.Start).TotalSeconds >= unknownAnnounceSeconds)
                        {
                            session.Announced = true;
                            announced.Add(session);
                        }
                    }

                    foreach (var key in unknown.Where(a => (now - a.Value.LastSeen).TotalSeconds >= exitGraceSeconds).Select(a => a.Key).ToList())
                    {
                        unknown.Remove(key);
                    }
                }
                else
                {
                    unknown.Clear();
                }
            }

            foreach (var session in started)
            {
                OnSessionConfirmed(session);
            }

            foreach (var session in credited)
            {
                CreditPlaytime(session);
            }

            foreach (var session in ended)
            {
                FinishSession(session);
            }

            foreach (var session in announced)
            {
                UnknownGameDetected?.Invoke(this, session);
            }
        }

        private void EndAllSessions()
        {
            List<TrackedSession> sessions;
            lock (stateLock)
            {
                sessions = tracked.Values.ToList();
                tracked.Clear();
                unknown.Clear();
            }

            sessions.ForEach(FinishSession);
        }

        private void PersistSession(TrackedSession session)
        {
            session.LastPersisted = DateTime.Now;
            gameroomDb.SaveActiveSession(new ActiveSession
            {
                Id = session.Id,
                GameId = session.GameId,
                Start = session.Start,
                LastSeen = session.LastSeen,
                Executable = session.Executable,
                ProcessId = session.ProcessId,
                ProcessStart = session.ProcessStart,
                CreditedSeconds = session.CreditedSeconds
            });
        }

        /// <summary>
        /// Adds the not yet credited part of a running session to the game's playtime.
        /// The first credit also counts the session as a play of the game.
        /// </summary>
        private void CreditPlaytime(TrackedSession session)
        {
            var game = database.Games.Get(session.GameId);
            if (game == null)
            {
                return;
            }

            var duration = session.Duration;
            if (duration > session.CreditedSeconds)
            {
                if (session.CreditedSeconds == 0)
                {
                    MarkPlayed(game, session);
                }

                game.Playtime += duration - session.CreditedSeconds;
                session.CreditedSeconds = duration;
                database.Games.Update(game);
            }

            PersistSession(session);
        }

        private void MarkPlayed(Game game, TrackedSession session)
        {
            game.PlayCount += 1;
            if (game.LastActivity == null || game.LastActivity < session.Start)
            {
                game.LastActivity = session.Start;
            }

            var completionSettings = database.GetCompletionStatusSettings();
            if (completionSettings.PlayedStatus != Constants.MaxGuidVal &&
                (game.CompletionStatusId == Guid.Empty || game.CompletionStatusId == completionSettings.DefaultStatus))
            {
                game.CompletionStatusId = completionSettings.PlayedStatus;
            }
        }

        private void OnSessionConfirmed(TrackedSession session)
        {
            var game = database.Games.Get(session.GameId);
            if (game == null)
            {
                return;
            }

            game.IsRunning = true;
            database.Games.Update(game);
            PersistSession(session);
            RememberExecutable(session.GameId, session.Executable);
            SessionStarted?.Invoke(this, session);
        }

        private void RememberExecutable(Guid gameId, string executable)
        {
            if (executable.IsNullOrWhiteSpace())
            {
                return;
            }

            var data = gameroomDb.GetOrCreateGameData(gameId);
            if (!data.KnownExecutables.Contains(executable, StringComparer.OrdinalIgnoreCase))
            {
                data.KnownExecutables.Add(executable);
                if (data.KnownExecutables.Count > 10)
                {
                    data.KnownExecutables.RemoveAt(0);
                }

                gameroomDb.SaveGameData(data);
            }
        }

        private void FinishSession(TrackedSession session)
        {
            gameroomDb.RemoveActiveSession(session.Id);
            var game = database.Games.Get(session.GameId);
            if (game == null)
            {
                return;
            }

            if (session.Confirmed)
            {
                game.IsRunning = false;
            }

            var duration = session.Duration;
            if (session.CreditedSeconds == 0 && (!session.Confirmed || duration < (ulong)settings.MinimumSessionLength))
            {
                logger.Info($"Ignoring short detected session of {game.Name} ({duration} s).");
                if (session.Confirmed)
                {
                    database.Games.Update(game);
                }

                return;
            }

            if (session.CreditedSeconds == 0)
            {
                MarkPlayed(game, session);
            }

            if (duration > session.CreditedSeconds)
            {
                game.Playtime += duration - session.CreditedSeconds;
            }

            database.Games.Update(game);
            var record = new GameSession
            {
                GameId = game.Id,
                GameName = game.Name,
                Start = session.Start,
                End = session.LastSeen,
                Duration = duration,
                Source = SessionSource.Detected,
                Executable = session.Executable
            };

            gameroomDb.AddSession(record);
            logger.Info($"Detected session of {game.Name} ended after {duration} seconds.");
            SessionEnded?.Invoke(this, record);
        }

        internal void RecoverActiveSessions()
        {
            foreach (var active in gameroomDb.GetActiveSessions())
            {
                try
                {
                    var game = database.Games.Get(active.GameId);
                    var session = new TrackedSession
                    {
                        Id = active.Id,
                        GameId = active.GameId,
                        GameName = game?.Name,
                        Start = active.Start,
                        LastSeen = active.LastSeen,
                        Executable = active.Executable,
                        ProcessId = active.ProcessId,
                        ProcessStart = active.ProcessStart,
                        Confirmed = true,
                        LastPersisted = DateTime.Now,
                        CreditedSeconds = active.CreditedSeconds
                    };

                    if (game != null && isProcessRunning(active.ProcessId, active.ProcessStart))
                    {
                        logger.Info($"Continuing detected session of {game.Name} started at {active.Start}.");
                        session.LastSeen = DateTime.Now;
                        lock (stateLock)
                        {
                            tracked[game.Id] = session;
                        }

                        game.IsRunning = true;
                        database.Games.Update(game);
                    }
                    else
                    {
                        // Gameroom wasn't running when the game exited, last persisted time is the best estimate.
                        FinishSession(session);
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to recover detected session {active.Id}");
                    gameroomDb.RemoveActiveSession(active.Id);
                }
            }
        }
    }
}
