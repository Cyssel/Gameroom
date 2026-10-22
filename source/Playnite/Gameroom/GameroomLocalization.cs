using Playnite.SDK;
using System;
using System.Linq;
using System.Windows;

namespace Playnite.Gameroom
{
    /// <summary>
    /// Loads strings of Gameroom features, compiled into Playnite.dll from Localization\Gameroom.
    /// English is always loaded first so untranslated strings fall back to it.
    /// </summary>
    public static class GameroomLocalization
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string resourceRoot = "pack://application:,,,/Playnite;component/Localization/Gameroom/";
        private static readonly string[] availableLanguages = { "en_US", "es_ES" };

        /// <summary>
        /// Culture of the language chosen in Gameroom, for day and month names. Numeric dates and times keep the
        /// regional format of Windows (<see cref="System.Globalization.CultureInfo.CurrentCulture"/>).
        /// </summary>
        public static System.Globalization.CultureInfo Culture => Localization.ApplicationLanguageCultureInfo;

        public static void Load(string language)
        {
            if (Application.Current == null)
            {
                return;
            }

            var dictionaries = Application.Current.Resources.MergedDictionaries;
            foreach (var existing in dictionaries.Where(a => a.Source?.OriginalString.StartsWith(resourceRoot) == true).ToList())
            {
                dictionaries.Remove(existing);
            }

            add("en_US");
            if (language != "en_US" && availableLanguages.Contains(language))
            {
                add(language);
            }

            void add(string lang)
            {
                try
                {
                    dictionaries.Add(new ResourceDictionary { Source = new Uri(resourceRoot + lang + ".xaml", UriKind.Absolute) });
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to load Gameroom localization {lang}");
                }
            }
        }
    }
}
