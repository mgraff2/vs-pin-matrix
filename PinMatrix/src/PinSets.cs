using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace PinMatrix
{
    /// <summary>
    /// A saved filter that can be switched on and off from the map screen: "Resin", "Copper",
    /// "Everything I marked while prospecting".
    ///
    /// WHAT IT STORES IS A QUESTION, NOT AN ANSWER. A set holds the *criteria* — title text, icons,
    /// colours, pinned — and never a list of waypoint keys. That is the whole point of the feature
    /// as asked for: pins added after the set was made are covered by it, so "Hide Resin" keeps
    /// working as you mark more resin. A stored key list would silently stop covering new pins and
    /// there would be nothing on screen to explain why.
    ///
    /// WHAT IT DELIBERATELY DOES NOT STORE is the radius filter and the visible/hidden filter. The
    /// radius is measured from wherever the player happens to be standing, so a saved button would
    /// mean something different every time it was pressed; and the hidden filter is precisely what
    /// the button *controls*, so baking it in would make the button argue with itself.
    ///
    /// Sets live in pinmatrix.json — global across worlds, unlike the hidden-pin state, which is
    /// per savegame (see <see cref="WaypointVisibility"/>). "Resin" means the same thing on every
    /// world; which pins are currently switched off does not.
    /// </summary>
    public class PinSet
    {
        /// <summary>Stable identity. Used for the button's dialog name, so it must survive renames.</summary>
        public string Id { get; set; }

        public string Name { get; set; } = "";

        /// <summary>Substring of the title, case-insensitive. Empty = no title condition.</summary>
        public string Search { get; set; } = "";

        /// <summary>Icon codes; empty = any icon. Matches <see cref="WpCommands.SafeIcon"/>.</summary>
        public List<string> Icons { get; set; } = new List<string>();

        /// <summary>"#rrggbb" values; empty = any colour.</summary>
        public List<string> Colors { get; set; } = new List<string>();

        public bool PinnedOnly { get; set; }

        /// <summary>Whether this set appears in the map's pin-set panel (the sets screen works either way).</summary>
        public bool ShowButton { get; set; } = true;

        /// <summary>
        /// Waypoint icon shown against this set's row in the map's pin-set panel. Empty = a plain
        /// colour chip instead, so every row still has something in the same place and the column
        /// scans vertically either way.
        ///
        /// Lit means some of the set is on the map, greyed means all of it is hidden — the icon is
        /// carrying the row's on/off state, which is why it is worth having one.
        /// </summary>
        public string ButtonIcon { get; set; } = "";

        public bool UsesIconButton => !string.IsNullOrWhiteSpace(ButtonIcon);

        /// <summary>
        /// True when nothing is filtered on at all, i.e. the set matches every pin you own.
        /// Allowed — "Hide everything" is a reasonable button — but called out in the editor,
        /// because arriving at it by accident and pressing the button is a memorable surprise.
        /// </summary>
        public bool MatchesEverything =>
            string.IsNullOrWhiteSpace(Search) && Icons.Count == 0 && Colors.Count == 0 && !PinnedOnly;

        public bool Matches(Waypoint wp)
        {
            if (wp == null) return false;
            if (!string.IsNullOrWhiteSpace(Search)
                && (wp.Title ?? "").IndexOf(Search, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (Icons.Count > 0 && !Icons.Contains(WpCommands.SafeIcon(wp.Icon))) return false;
            if (Colors.Count > 0 && !Colors.Contains(WpCommands.ColorHex(wp.Color))) return false;
            if (PinnedOnly && !wp.Pinned) return false;
            return true;
        }

        /// <summary>One line describing the criteria, for the sets list and the button tooltip.</summary>
        public string CriteriaSummary()
        {
            if (MatchesEverything) return "every pin";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Search)) parts.Add($"name has \"{Search}\"");
            if (Icons.Count > 0) parts.Add(Icons.Count == 1 ? $"icon {Icons[0]}" : $"{Icons.Count} icons");
            if (Colors.Count > 0) parts.Add(Colors.Count == 1 ? $"colour {Colors[0]}" : $"{Colors.Count} colours");
            if (PinnedOnly) parts.Add("pinned only");
            return string.Join(", ", parts);
        }

        /// <summary>
        /// The same summary for a rich-text element, with the colours drawn as swatches instead of
        /// spelled out as hex.
        ///
        /// "colour #8a6fe8" is a fact you have to decode; a swatch is the fact itself. The plain
        /// <see cref="CriteriaSummary"/> stays for tooltips and anywhere else that takes no markup —
        /// the two must keep saying the same thing, so they are written next to each other.
        /// Escaped, because a set's name filter is player text and could contain markup.
        /// </summary>
        public string CriteriaSummaryVtml()
        {
            if (MatchesEverything) return "every pin";

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Search)) parts.Add($"name has \"{WpCommands.VtmlEscape(Search)}\"");
            if (Icons.Count > 0) parts.Add(Icons.Count == 1 ? $"icon {WpCommands.VtmlEscape(Icons[0])}" : $"{Icons.Count} icons");

            if (Colors.Count > 0)
            {
                // A cap, because a set filtering on a dozen colours would push everything after it
                // off the row. Past the cap the count carries the rest.
                const int MaxSwatches = 6;
                var sb = new StringBuilder(Colors.Count == 1 ? "colour " : "colours ");
                for (int i = 0; i < Colors.Count && i < MaxSwatches; i++)
                {
                    sb.Append('<').Append(ColorSwatchComponent.TagName)
                      .Append(" color=\"").Append(WpCommands.VtmlEscape(Colors[i])).Append("\"/>");
                }
                if (Colors.Count > MaxSwatches) sb.Append(" +").Append(Colors.Count - MaxSwatches);
                parts.Add(sb.ToString());
            }

            if (PinnedOnly) parts.Add("pinned only");
            return string.Join(", ", parts);
        }


        public PinSet Copy() => new PinSet
        {
            Id = Id,
            Name = Name,
            Search = Search,
            Icons = new List<string>(Icons),
            Colors = new List<string>(Colors),
            PinnedOnly = PinnedOnly,
            ShowButton = ShowButton,
            ButtonIcon = ButtonIcon
        };

        /// <summary>
        /// Ids are minted from a counter over the existing sets rather than from a Guid or a clock:
        /// they end up inside composer names and in the layout store's keys, where something short
        /// and stable is worth more than something globally unique. Nothing outside this client
        /// ever sees one.
        /// </summary>
        public static string NewId(IEnumerable<PinSet> existing)
        {
            var taken = new HashSet<string>(existing.Select(s => s.Id ?? ""));
            for (int i = 1; i < 10000; i++)
            {
                string id = "s" + i;
                if (!taken.Contains(id)) return id;
            }
            return "s" + taken.Count;
        }
    }

    /// <summary>
    /// Evaluates sets against the live waypoint list and switches them on and off.
    ///
    /// THE TOGGLE RULE: a set's button reads "Hide X" while *any* matching pin is still visible, and
    /// "Show X" only once every one of them is switched off. So one press always lands on a clean
    /// state — hide the stragglers, or bring the whole set back — and the label can never disagree
    /// with what the map is showing. The alternative (majority wins) flips the label under the
    /// player mid-way through hiding things by hand, which reads as a bug.
    ///
    /// COUNTS ARE CACHED, ON PURPOSE. Button labels carry a live count, and the map-screen watcher
    /// asks for them four times a second. Recomputing per label would walk every waypoint once per
    /// set per tick; <see cref="Recount"/> instead walks the list once for all sets and the labels
    /// read the cache. It is called from the map watcher and immediately after any toggle, so a
    /// press updates its own button in the same frame it lands.
    /// </summary>
    public class PinSetService
    {
        readonly PinMatrixConfig config;
        readonly WaypointService svc;
        readonly WaypointVisibility visibility;
        readonly Action save;

        readonly Dictionary<string, int> total = new Dictionary<string, int>();
        readonly Dictionary<string, int> visible = new Dictionary<string, int>();

        public PinSetService(PinMatrixConfig config, WaypointService svc, WaypointVisibility visibility, Action save)
        {
            this.config = config;
            this.svc = svc;
            this.visibility = visibility;
            this.save = save;
        }

        public List<PinSet> All => config.PinSets;

        public PinSet ById(string id) => config.PinSets.FirstOrDefault(s => s.Id == id);

        /// <summary>Sets that should appear in the map panel, in list order.</summary>
        public List<PinSet> Buttoned => config.PinSets.Where(s => s.ShowButton).ToList();

        /// <summary>Whether hiding works at all on this game version — the panel is dead weight without it.</summary>
        public bool Available => visibility.Available;

        // ------------------------------------------------------------------ counting

        /// <summary>One pass over the waypoints for every set at once. See the class remarks.</summary>
        public void Recount()
        {
            total.Clear();
            visible.Clear();

            var sets = config.PinSets;
            if (sets.Count == 0) return;

            List<Waypoint> own;
            try { own = svc.Own; }
            catch (Exception) { return; }   // not in a world yet

            foreach (var s in sets)
            {
                if (string.IsNullOrEmpty(s.Id)) continue;
                total[s.Id] = 0;
                visible[s.Id] = 0;
            }

            foreach (var wp in own)
            {
                string key = PinKey.KeyOf(wp);
                bool hidden = visibility.IsHidden(key);
                foreach (var s in sets)
                {
                    if (string.IsNullOrEmpty(s.Id) || !s.Matches(wp)) continue;
                    total[s.Id] = total[s.Id] + 1;
                    if (!hidden) visible[s.Id] = visible[s.Id] + 1;
                }
            }
        }

        public int TotalCount(PinSet s) => s?.Id != null && total.TryGetValue(s.Id, out int n) ? n : 0;
        public int VisibleCount(PinSet s) => s?.Id != null && visible.TryGetValue(s.Id, out int n) ? n : 0;
        public int HiddenCount(PinSet s) => TotalCount(s) - VisibleCount(s);

        /// <summary>True when pressing the button would hide rather than show (see the toggle rule).</summary>
        public bool WouldHide(PinSet s) => VisibleCount(s) > 0;

        /// <summary>
        /// What a panel row's icon is tinted with while it is on.
        ///
        /// A set that filters on exactly one colour is drawn in that colour, so the row and the pins
        /// it controls read as the same thing at a glance. Anything else is white: a set spanning
        /// three colours has no honest single colour, and picking one would misdescribe it.
        /// </summary>
        public static double[] ButtonTint(PinSet s)
        {
            if (s != null && s.Colors.Count == 1)
            {
                var rgb = ColorSwatchComponent.ParseHex(s.Colors[0]);
                return new[] { rgb[0], rgb[1], rgb[2], 1.0 };
            }
            return new double[] { 1, 1, 1, 1 };
        }

        /// <summary>
        /// The tooltip behind an icon button - it is the only place the set's name appears, so it
        /// also has to carry what the label would have said, and what a click will do.
        /// </summary>
        public string ButtonTooltip(PinSet s)
        {
            if (s == null) return "";
            string name = string.IsNullOrWhiteSpace(s.Name) ? "set" : s.Name.Trim();
            int n = TotalCount(s);
            if (n == 0) return $"{name}\nNo pins match this set right now.\n({s.CriteriaSummary()})";

            int vis = VisibleCount(s);
            string state = vis == n ? $"all {n} showing"
                : vis == 0 ? $"all {n} hidden"
                : $"{vis} of {n} showing";
            string action = WouldHide(s) ? "Click to hide them." : "Click to show them.";
            return $"{name}\n{state}. {action}\n({s.CriteriaSummary()})";
        }

        /// <summary>
        /// The button's face. A set that matches nothing right now says so plainly instead of
        /// offering to show or hide zero pins — "Show Resin (0)" on a world with no resin marked is
        /// a button that looks broken.
        /// </summary>
        public string ButtonLabel(PinSet s)
        {
            if (s == null) return "";
            string name = string.IsNullOrWhiteSpace(s.Name) ? "set" : s.Name.Trim();
            int n = TotalCount(s);
            if (n == 0) return $"{name} (0)";
            return WouldHide(s) ? $"Hide {name} ({VisibleCount(s)})" : $"Show {name} ({n})";
        }

        // ------------------------------------------------------------------ acting

        /// <summary>
        /// Hides or shows everything the set currently matches. Returns how many pins changed;
        /// 0 means nothing matched, which the caller reports rather than silently doing nothing.
        /// </summary>
        public int Toggle(PinSet s) => Apply(s, WouldHide(s));

        /// <summary>Explicit hide/show, for the sets screen's own two buttons.</summary>
        public int Apply(PinSet s, bool hide)
        {
            if (s == null || !visibility.Available) return 0;

            List<Waypoint> own;
            try { own = svc.Own; }
            catch (Exception) { return 0; }

            var keys = own.Where(s.Matches).Select(PinKey.KeyOf).ToList();
            if (keys.Count == 0) { Recount(); return 0; }

            int changed = visibility.Set(keys, hide);
            Recount();
            return changed;
        }

        // ------------------------------------------------------------------ editing

        public PinSet Add(PinSet set)
        {
            if (set == null) return null;
            set.Id = PinSet.NewId(config.PinSets);
            config.PinSets.Add(set);
            Store();
            return set;
        }

        /// <summary>Writes an edited copy back over the stored set, keeping its id and hence its button.</summary>
        public void Update(PinSet edited)
        {
            if (edited?.Id == null) return;
            int i = config.PinSets.FindIndex(s => s.Id == edited.Id);
            if (i < 0) return;
            config.PinSets[i] = edited;
            Store();
        }

        public void Remove(string id)
        {
            if (config.PinSets.RemoveAll(s => s.Id == id) > 0) Store();
        }

        /// <summary>Moves a set up or down the list, which is also the order of its map buttons.</summary>
        public void Move(string id, int delta)
        {
            int i = config.PinSets.FindIndex(s => s.Id == id);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= config.PinSets.Count) return;
            var set = config.PinSets[i];
            config.PinSets.RemoveAt(i);
            config.PinSets.Insert(j, set);
            Store();
        }

        void Store()
        {
            config.Clamp();
            save?.Invoke();
            Recount();
        }

        /// <summary>
        /// Change-detection fingerprint for the map buttons: which sets exist, in what order, and
        /// what each button would read. The button windows rebuild when this changes, so a rename,
        /// a reorder or a pin appearing in a set all reach the map without anything polling labels.
        /// </summary>
        public string ButtonSignature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Buttoned)
            {
                sb.Append(s.Id).Append('=').Append(s.ButtonIcon).Append('/')
                  .Append(ButtonLabel(s)).Append('/').Append(WouldHide(s) ? '1' : '0').Append(';');
            }
            return sb.ToString();
        }
    }
}
