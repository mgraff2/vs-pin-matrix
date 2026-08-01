using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>A parsed import entry, coordinates already converted to absolute world coords.</summary>
    public class ImportPin
    {
        public string Name;
        public string Icon;
        public int Color;
        public double AbsX;
        public double AbsY;
        public double AbsZ;
        public bool Pinned;
    }

    public class ImportResult
    {
        public List<ImportPin> Valid = new List<ImportPin>();
        public List<string> Errors = new List<string>();
    }

    /// <summary>
    /// JSON export/import. Format: X/Z spawn-relative (what players see in the coordinate HUD), Y absolute,
    /// colors as "#rrggbb" hex strings. Files live under VintagestoryData/ModData/pinmatrix.
    /// </summary>
    public static class Porter
    {
        public static string Folder(ICoreClientAPI capi) => capi.GetOrCreateDataPath(Path.Combine("ModData", "pinmatrix"));

        public static string Export(ICoreClientAPI capi, WaypointService svc, IEnumerable<Waypoint> waypoints, string filePrefix)
        {
            var list = waypoints.Select(wp => new JObject
            {
                ["name"] = wp.Title ?? "",
                ["icon"] = WpCommands.SafeIcon(wp.Icon),
                ["color"] = WpCommands.ColorHex(wp.Color),
                ["x"] = Math.Round(svc.RelX(wp.Position.X), 1),
                ["y"] = Math.Round(wp.Position.Y, 1),
                ["z"] = Math.Round(svc.RelZ(wp.Position.Z), 1),
                ["pinned"] = wp.Pinned
            });

            var doc = new JObject
            {
                ["format"] = "pinmatrix",
                ["version"] = 1,
                ["coords"] = "x,z spawn-relative; y absolute",
                ["exportedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"),
                ["waypoints"] = new JArray(list.Cast<object>().ToArray())
            };

            string file = Path.Combine(Folder(capi), $"{filePrefix}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(file, doc.ToString(Formatting.Indented));
            return file;
        }

        public static ImportResult Import(string path, WaypointService svc)
        {
            var result = new ImportResult();

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                result.Errors.Add("Cannot read file: " + e.Message);
                return result;
            }

            JArray arr;
            try
            {
                var token = JToken.Parse(json);
                if (token is JObject obj && obj["waypoints"] is JArray wrapped) arr = wrapped;
                else if (token is JArray bare) arr = bare;
                else
                {
                    result.Errors.Add("Unrecognized JSON layout: expected an array or an object with a 'waypoints' array.");
                    return result;
                }
            }
            catch (Exception e)
            {
                result.Errors.Add("Invalid JSON: " + e.Message);
                return result;
            }

            for (int i = 0; i < arr.Count; i++)
            {
                string where = $"entry {i + 1}";
                if (!(arr[i] is JObject entry))
                {
                    result.Errors.Add($"{where}: not an object");
                    continue;
                }

                double? x = ReadNumber(entry, "x"), y = ReadNumber(entry, "y"), z = ReadNumber(entry, "z");
                if (x == null || y == null || z == null)
                {
                    result.Errors.Add($"{where}: missing or non-numeric x/y/z");
                    continue;
                }

                int? color = ParseColor(entry["color"]);
                if (color == null)
                {
                    result.Errors.Add($"{where}: missing or invalid color (use \"#rrggbb\")");
                    continue;
                }

                result.Valid.Add(new ImportPin
                {
                    Name = entry.Value<string>("name") ?? "",
                    Icon = WpCommands.SafeIcon(entry.Value<string>("icon")),
                    Color = color.Value,
                    AbsX = svc.ClampAbsX(svc.AbsX(x.Value)),
                    AbsY = svc.ClampY(y.Value),
                    AbsZ = svc.ClampAbsZ(svc.AbsZ(z.Value)),
                    Pinned = entry["pinned"]?.Type == JTokenType.Boolean && entry.Value<bool>("pinned")
                });
            }

            return result;
        }

        static double? ReadNumber(JObject obj, string key)
        {
            var token = obj[key];
            if (token == null) return null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<double>();
            if (token.Type == JTokenType.String
                && double.TryParse(token.Value<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            return null;
        }

        static int? ParseColor(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            if (token.Type == JTokenType.String)
            {
                string s = token.Value<string>()?.Trim() ?? "";
                if (s.StartsWith("#")) s = s.Substring(1);
                if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                {
                    return unchecked((int)0xFF000000) | rgb;
                }
            }
            return null;
        }
    }
}
