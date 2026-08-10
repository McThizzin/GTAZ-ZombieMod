using System;
using System.Globalization;
using System.IO;
using System.Linq;
using GTA;
using GTA.Math;

namespace GTAZ.Data.Serialization
{
    // Writes .ini via SHVDN's ScriptSettings, not JSON. All values are stored as strings under a
    // [Data] section and cast back to the property's real type on load.
    public static class IniSerializer
    {
        private const string BasePath = "./scripts/GTAZ/";

        public static void Serialize<T>(string fileName, T data)
        {
            string path = Path.Combine(BasePath, fileName + ".ini");
            Directory.CreateDirectory(BasePath);
            var settings = ScriptSettings.Load(path);

            var props = typeof(T).GetProperties();
            foreach (var prop in props)
            {
                if (!prop.CanRead || !prop.CanWrite || prop.GetMethod?.IsStatic is true)
                    continue;

                object val = prop.GetValue(data);
                if (val == null)
                    continue;

                // Vector3 has no round-trippable ToString(), so store it as an invariant "X,Y,Z".
                // Collections (List<T>/arrays) are stored as comma-separated items.
                string text;
                if (val is Vector3 v)
                {
                    text = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", v.X, v.Y, v.Z);
                }
                else if (val is System.Collections.IEnumerable seq && val is not string)
                {
                    text = string.Join(",", seq.Cast<object>()
                        .Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
                }
                else
                {
                    text = Convert.ToString(val, CultureInfo.InvariantCulture);
                }

                settings.SetValue("Data", prop.Name, text);
            }

            settings.Save();
        }

        public static T Deserialize<T>(string fileName) where T : new()
        {
            string path = Path.Combine(BasePath, fileName + ".ini");
            if (!File.Exists(path))
                return new T();

            var settings = ScriptSettings.Load(path);
            var result = new T();
            var props = typeof(T).GetProperties();

            foreach (var prop in props)
            {
                if (!prop.CanRead || !prop.CanWrite || prop.GetMethod?.IsStatic is true)
                    continue;

                string raw = settings.GetValue("Data", prop.Name, string.Empty);
                if (string.IsNullOrEmpty(raw))
                    continue;

                try
                {
                    // ScriptSettings.GetValue<T> only casts correctly when T is the real property
                    // type; passing an object default returned every value as a string and blew
                    // up on non-string properties. Parse by the property's own type instead.
                    object loaded;
                    if (prop.PropertyType == typeof(Vector3))
                    {
                        loaded = ParseVector3(raw);
                    }
                    else if (prop.PropertyType != typeof(string)
                             && typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType))
                    {
                        loaded = ParseList(prop.PropertyType, raw);
                    }
                    else if (prop.PropertyType.IsEnum)
                    {
                        loaded = Enum.Parse(prop.PropertyType, raw, true);
                    }
                    else
                    {
                        loaded = Convert.ChangeType(raw, prop.PropertyType, CultureInfo.InvariantCulture);
                    }
                    prop.SetValue(result, loaded);
                }
                catch
                {
                    // Keep the default when a value can't be cast (e.g. type changed between versions).
                }
            }

            return result;
        }

        private static Vector3 ParseVector3(string text)
        {
            string[] parts = text.Split(',');
            return new Vector3(
                float.Parse(parts[0], CultureInfo.InvariantCulture),
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture));
        }

        // Rebuilds a collection from a comma-separated string (inverse of the Serialize path).
        // Only supports concrete List<T>/array types with a parameterless constructor.
        private static object ParseList(Type listType, string raw)
        {
            Type elementType = listType.IsArray
                ? listType.GetElementType()
                : listType.GetGenericArguments()[0];

            var list = (System.Collections.IList)Activator.CreateInstance(listType);
            foreach (string part in raw.Split(','))
            {
                if (string.IsNullOrEmpty(part))
                    continue;
                list.Add(Convert.ChangeType(part, elementType, CultureInfo.InvariantCulture));
            }
            return list;
        }

        public static bool Exists(string fileName)
        {
            return File.Exists(Path.Combine(BasePath, fileName + ".ini"));
        }
    }
}
