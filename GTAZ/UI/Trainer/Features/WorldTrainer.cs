using System;
using System.Collections.Generic;
using GTA;
using GTA.Chrono;

namespace GTAZ.UI.Trainer.Features
{
    public static class WorldTrainer
    {
        private static readonly string[] WeatherNames =
        [
            "ExtraSunny", "Clear", "Clouds", "Smog", "Foggy", "Overcast", "Raining",
            "ThunderStorm", "Clearing", "Neutral", "Snowing", "Blizzard", "Snowlight",
            "Christmas", "Halloween"
        ];

        public static IReadOnlyList<string> WeatherOptions => WeatherNames;

        public static void SetWeather(string name)
        {
            if (Enum.TryParse(name, true, out Weather weather))
            {
                World.Weather = weather;
                GTA.UI.Notification.PostTicker($"Weather set to: ~g~{name}", false);
            }
            else
            {
                GTA.UI.Notification.PostTicker($"~r~Invalid weather: ~r~{name}", false);
            }
        }

        public static void FreezeTime(bool enabled)
        {
            GameClock.IsPaused = enabled;
            PlayerTrainer.ShowStatus("Freeze Time: ", enabled);
        }

        public static void SetHour(int hour)
        {
            GameClock.Hour = hour;
        }

        public static void SetMinute(int minute)
        {
            GameClock.Minute = minute;
        }

        public static void SetSecond(int second)
        {
            GameClock.Second = second;
        }
    }
}
