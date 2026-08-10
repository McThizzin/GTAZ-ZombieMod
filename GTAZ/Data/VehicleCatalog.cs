using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GTAZ.Core;

namespace GTAZ.Data
{
    public static class VehicleCatalog
    {
        private static readonly Dictionary<string, List<string>> _byCategory = [];
        private static readonly List<string> _categories = [];
        private static bool _loaded;

        private static readonly string[] _fallbackModels =
        [
            "adder", "zentorno", "bati", "sanchez", "police", "firetruck", "bmx", "seashark"
        ];

        public static IReadOnlyList<string> Categories => _categories;
        public static int Count => _byCategory.Values.Sum(list => list.Count);

        public static void Load()
        {
            if (_loaded)
                return;
            _loaded = true;

            string path = ResolvePath();
            if (path is null)
            {
                Logger.Warn("vehicles.ini not found - using built-in fallback vehicle list.");
                AddFallback();
                return;
            }

            try
            {
                string current = null;
                foreach (var rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                        continue;

                    if (line[0] == '[' && line.EndsWith("]"))
                    {
                        current = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }

                    if (current is null)
                        continue;

                    if (!_byCategory.TryGetValue(current, out var list))
                    {
                        list = [];
                        _byCategory[current] = list;
                        _categories.Add(current);
                    }
                    list.Add(line);
                }

                Logger.Info($"Loaded {Count} vehicles across {_categories.Count} categories from {path}");
                if (Count == 0)
                    AddFallback();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load vehicles.ini: {ex.Message}");
                AddFallback();
            }
        }

        private static string ResolvePath()
        {
            foreach (var candidate in new[] { "./scripts/vehicles.ini", "./scripts/GTAZ/vehicles.ini" })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        private static void AddFallback()
        {
            _byCategory["Fallback"] = [.. _fallbackModels];
            _categories.Add("Fallback");
        }

        public static IReadOnlyList<string> GetModels(string category)
        {
            return _byCategory.TryGetValue(category, out var list)
                ? list
                : [];
        }
    }
}
