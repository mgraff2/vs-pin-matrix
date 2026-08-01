using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace PinMatrix
{
    public class BinEntry
    {
        public string Title;
        public string Icon;
        public int Color;
        public double X;   // absolute world coords
        public double Y;
        public double Z;
        public bool Pinned;
        public string DeletedAtUtc;
        public string Op;
    }

    /// <summary>
    /// Client-side recycle bin: deleted waypoints are stored here (JSON under VintagestoryData/ModData/pinmatrix)
    /// and can be restored as new waypoints. The server never knows about it.
    /// </summary>
    public class RecycleBin
    {
        readonly ICoreClientAPI capi;
        readonly PinMatrixConfig config;
        readonly string filePath;
        List<BinEntry> entries = new List<BinEntry>();

        public IReadOnlyList<BinEntry> Entries => entries;

        public RecycleBin(ICoreClientAPI capi, PinMatrixConfig config)
        {
            this.capi = capi;
            this.config = config;
            string folder = capi.GetOrCreateDataPath(Path.Combine("ModData", "pinmatrix"));
            filePath = Path.Combine(folder, "recyclebin.json");
            Load();
        }

        void Load()
        {
            try
            {
                if (!File.Exists(filePath)) return;
                entries = JsonConvert.DeserializeObject<List<BinEntry>>(File.ReadAllText(filePath)) ?? new List<BinEntry>();
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pinmatrix] Could not read recycle bin file, starting empty: {0}", e.Message);
                try { File.Copy(filePath, filePath + ".broken", true); } catch { /* best effort */ }
                entries = new List<BinEntry>();
            }
        }

        void Save()
        {
            try
            {
                File.WriteAllText(filePath, JsonConvert.SerializeObject(entries, Formatting.Indented));
            }
            catch (Exception e)
            {
                capi.Logger.Error("[pinmatrix] Could not save recycle bin: {0}", e.Message);
            }
        }

        public void Add(IEnumerable<Waypoint> waypoints, string op)
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
            foreach (var wp in waypoints)
            {
                entries.Add(new BinEntry
                {
                    Title = wp.Title,
                    Icon = wp.Icon,
                    Color = wp.Color,
                    X = wp.Position.X,
                    Y = wp.Position.Y,
                    Z = wp.Position.Z,
                    Pinned = wp.Pinned,
                    DeletedAtUtc = now,
                    Op = op
                });
            }
            // entries are chronological; prune oldest past the cap
            int excess = entries.Count - config.RecycleBinMaxEntries;
            if (excess > 0) entries.RemoveRange(0, excess);
            Save();
        }

        public void Remove(IEnumerable<BinEntry> toRemove)
        {
            var set = new HashSet<BinEntry>(toRemove);
            entries = entries.Where(e => !set.Contains(e)).ToList();
            Save();
        }

        public void Clear()
        {
            entries.Clear();
            Save();
        }
    }
}
