using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Gameroom.Achievements
{
    /// <summary>
    /// Achievement as defined by the game on Steam.
    /// </summary>
    public class AchievementDefinition
    {
        public string ApiName { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Hidden { get; set; }
        /// <summary>Icon file name on the Steam CDN.</summary>
        public string Icon { get; set; }
        public string IconGray { get; set; }
        /// <summary>Percentage of Steam players who unlocked it.</summary>
        public double? GlobalPercent { get; set; }
    }

    public class AchievementSchema
    {
        public string AppId { get; set; }
        public string Language { get; set; }
        public DateTime Updated { get; set; }
        public List<AchievementDefinition> Achievements { get; set; } = new List<AchievementDefinition>();
    }

    /// <summary>
    /// Unlock state of one achievement, as stored by Steam or a Steam emulator.
    /// </summary>
    public class AchievementProgress
    {
        public string ApiName { get; set; }
        public bool Unlocked { get; set; }
        public DateTime? UnlockTime { get; set; }
        public int CurrentProgress { get; set; }
        public int MaxProgress { get; set; }
    }

    public enum AchievementSource
    {
        Steam,
        Goldberg,
        Codex,
        Rune,
        OnlineFix,
        Empress
    }

    public class AchievementSourceFile
    {
        public string Path { get; set; }
        public AchievementSource Source { get; set; }

        public AchievementSourceFile(string path, AchievementSource source)
        {
            Path = path;
            Source = source;
        }
    }

    /// <summary>
    /// Achievement of a game with its unlock state, ready to be shown.
    /// </summary>
    public class GameAchievement
    {
        public AchievementDefinition Definition { get; set; }
        public bool Unlocked { get; set; }
        public DateTime? UnlockTime { get; set; }
        public int CurrentProgress { get; set; }
        public int MaxProgress { get; set; }
        /// <summary>Local file of the icon matching the unlock state, null when it couldn't be downloaded.</summary>
        public string IconPath { get; set; }
    }

    public class GameAchievements
    {
        public string AppId { get; set; }
        public List<GameAchievement> Items { get; set; } = new List<GameAchievement>();
        public List<AchievementSource> Sources { get; set; } = new List<AchievementSource>();
        public int Total => Items.Count;
        public int UnlockedCount => Items.Count(a => a.Unlocked);
        public double Percent => Total == 0 ? 0 : 100.0 * UnlockedCount / Total;
    }

    public class RecentAchievement
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public GameAchievement Achievement { get; set; }
    }

    public class AchievementsUnlockedEventArgs : EventArgs
    {
        public string AppId { get; set; }
        public AchievementSource Source { get; set; }
        public List<AchievementProgress> Unlocked { get; set; }
    }
}
