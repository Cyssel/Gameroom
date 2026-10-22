using Playnite.Gameroom;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.Windows;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.DesktopApp.ViewModels
{
    public class GameSessionItem
    {
        public GameSession Session { get; set; }
        public string StartText => Session.Start.ToString("g");
        public string EndText => Session.End.ToString("t");
        public string DurationText => GameroomService.FormatDuration(Session.Duration);
        public string SourceText => ResourceProvider.GetString("LOCGameroomSessionSource" + Session.Source);
        public string Executable => Session.Executable;
    }

    public class GameSessionsViewModel : ObservableObject
    {
        private readonly IWindowFactory window;
        private readonly Game game;

        public string Title => $"{ResourceProvider.GetString("LOCGameroomSessionsTitle")} - {game.Name}";
        public List<GameSessionItem> Sessions { get; }
        public bool HasSessions => Sessions.Count > 0;
        public string Summary { get; }

        public RelayCommand CloseCommand => new RelayCommand(() => window.Close());

        public GameSessionsViewModel(IWindowFactory window, GameroomService service, Game game)
        {
            this.window = window;
            this.game = game;
            Sessions = service.Database.GetSessions(game.Id).Select(a => new GameSessionItem { Session = a }).ToList();
            var total = (ulong)Sessions.Sum(a => (long)a.Session.Duration);
            Summary = string.Format(ResourceProvider.GetString("LOCGameroomSessionsTotal"), Sessions.Count, GameroomService.FormatDuration(total));
        }

        public void OpenView()
        {
            window.CreateAndOpenDialog(this);
        }
    }
}
