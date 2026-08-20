using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

namespace PinMatrix
{
    /// <summary>
    /// The Pin sets screens, and the Tools cabinet that made room for them.
    ///
    /// A set is a saved filter with a name. Its row in the map's pin-set panel toggles everything
    /// the filter matches *right now* — see <see cref="PinSet"/> for why the criteria are stored and
    /// the pins are not. These screens are where sets are made and edited; the panel is where they
    /// are used, and Hide / Show here do the same job for anyone who would rather not leave the
    /// editor.
    /// </summary>
    public partial class GuiDialogPinMatrix
    {
        /// <summary>The set being edited on the EditSet screen; null while creating a new one.</summary>
        PinSet editingSet;
        bool editingIsNew;

        PinSetService SetsService => ModSystem?.Sets;

        // ------------------------------------------------------------------ entry points

        void OpenPinSets()
        {
            SetsService?.Recount();
            screen = PmScreen.PinSets;
            Recompose();
        }

        /// <summary>
        /// "Save as set..." on the filter bar. This is the natural moment to make one: the player
        /// has just built the filter by hand and can see exactly what it catches, so the editor
        /// opens pre-filled from it rather than empty.
        ///
        /// The radius and the visible/hidden filter are left behind on purpose (see
        /// <see cref="PinSet"/>) — a button whose meaning depends on where you were standing when
        /// you pressed it is not a button anyone can rely on.
        /// </summary>
        bool OnSaveFilterAsSet()
        {
            if (config.PinSets.Count >= PinMatrixConfig.MaxPinSets)
            {
                notice = $"That is the maximum of {PinMatrixConfig.MaxPinSets} sets — delete one first.";
                UpdateMatrixDynamic();
                return true;
            }

            editingSet = new PinSet
            {
                Name = SuggestedSetName(),
                Search = searchText,
                Icons = iconFilter.ToList(),
                Colors = colorFilter.ToList(),
                PinnedOnly = pinnedOnly
            };
            editingIsNew = true;
            screen = PmScreen.EditSet;
            Recompose();
            return true;
        }

        /// <summary>
        /// A first guess at the name, so the common case is type-nothing-and-save. The search text
        /// is what the player actually typed, so it is the best name available; a single icon is
        /// the next best. Everything else is unnameable and left blank rather than guessed at.
        /// </summary>
        string SuggestedSetName()
        {
            if (!string.IsNullOrWhiteSpace(searchText)) return searchText.Trim();
            if (iconFilter.Count == 1) return iconFilter.First();
            return "";
        }

        // ------------------------------------------------------------------ the sets list

        const double SetRowH = 32;

        void ComposePinSets(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            var tip = CairoFont.WhiteDetailText();
            double y = 42;

            var sets = SetsService?.All ?? new List<PinSet>();

            // What a set IS goes in a tooltip on the header, not across the top of the screen: it is
            // an explanation, and it is read once. What stays on the screen is anything that tells
            // you why the thing you are looking for is not there — see the two notices below.
            c.AddStaticText("Your sets", CairoFont.WhiteSmallishText(), EB(4, y, 200, 26));
            c.AddHoverText(
                "A set is a saved filter with a name. Every set you keep here becomes a row in the "
                + "pin-set panel down the right of the world map: click it to hide everything it "
                + "matches, click again to bring it back. Hiding re-runs the filter at that moment, so "
                + "pins you mark later are covered by a set you made today — nothing stores a list of "
                + "pins. Distance and the visible/hidden filter are deliberately not saved: one depends "
                + "on where you were standing, and the other is what a set already controls.",
                tip, 360, EB(4, y, 200, 26).FlatCopy(), "tipwhatisaset");
            y += 30;

            if (visibility != null && !visibility.Available)
            {
                c.AddStaticText(
                    "Hiding pins is not available on this game version — see the client log.",
                    tip, EB(4, y, DW - 8, 26));
                y += 28;
            }

            if (sets.Count == 0)
            {
                c.AddStaticText("No sets yet. Filter the table how you like, then press \"Save as set...\".",
                    font, EB(4, y + 8, DW - 8, 30));
                y += 44;
            }

            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                string id = set.Id;
                int total = SetsService.TotalCount(set);
                int hidden = SetsService.HiddenCount(set);

                c.AddStaticText(set.Name, font, EB(4, y + 6, 190, 26));
                c.AddStaticText(set.CriteriaSummary(), tip, EB(198, y + 7, 300, 26));
                c.AddStaticText(total == 0 ? "no pins" : $"{total} pins, {hidden} hidden", tip, EB(502, y + 7, 130, 26));

                c.AddSmallButton("Hide", () => { ApplySetFromScreen(id, true); return true; }, EB(628, y, 56, 26), EnumButtonStyle.Small);
                c.AddSmallButton("Show", () => { ApplySetFromScreen(id, false); return true; }, EB(688, y, 58, 26), EnumButtonStyle.Small);
                c.AddSmallButton("Edit", () => { OpenEditSet(id); return true; }, EB(750, y, 52, 26), EnumButtonStyle.Small);
                // Up/down is also the order the buttons appear in on the map screen, which is the
                // only reason ordering is worth a control at all.
                c.AddSmallButton("^", () => { MoveSet(id, -1); return true; }, EB(806, y, 26, 26), EnumButtonStyle.Small);
                c.AddSmallButton("v", () => { MoveSet(id, 1); return true; }, EB(836, y, 26, 26), EnumButtonStyle.Small);
                c.AddSmallButton("X", () => { DeleteSet(id); return true; }, EB(866, y, 26, 26), EnumButtonStyle.Small);

                y += SetRowH;
            }

            y += 10;
            c.AddDynamicText(notice, font, EB(4, y + 5, DW - 200, 24), "setsnotice");
            c.AddSmallButton("Back", () => { BackToMatrix(); return true; }, EB(DW - 100, y, 96, 28));
        }

        void ApplySetFromScreen(string id, bool hide)
        {
            var set = SetsService?.ById(id);
            if (set == null) return;

            if (visibility != null && !visibility.Available)
            {
                notice = "Hiding pins is not available on this game version — see the client log.";
                Recompose();
                return;
            }

            int changed = SetsService.Apply(set, hide);
            notice = changed == 0
                ? $"Nothing to {(hide ? "hide" : "show")} — no pins match \"{set.Name}\" right now."
                : $"{(hide ? "Hid" : "Showed")} {changed} pin{(changed == 1 ? "" : "s")} in \"{set.Name}\".";
            ModSystem?.RefreshSetPanel();
            RefreshData();
            Recompose();
        }

        void MoveSet(string id, int delta)
        {
            SetsService?.Move(id, delta);
            ModSystem?.RefreshSetPanel();
            Recompose();
        }

        /// <summary>
        /// Deleting a set never touches a waypoint, and never un-hides anything either. Both would
        /// be surprises: the set is a saved question, and the pins it last switched off are still
        /// switched off — visible in the "N hidden" counter on the matrix, and recoverable from the
        /// Show: hidden filter. Saying so is cheaper than guessing which the player wanted.
        /// </summary>
        void DeleteSet(string id)
        {
            var set = SetsService?.ById(id);
            if (set == null) return;

            int hidden = SetsService.HiddenCount(set);
            SetsService.Remove(id);
            ModSystem?.RefreshSetPanel();
            notice = hidden > 0
                ? $"Deleted \"{set.Name}\". Its {hidden} hidden pin{(hidden == 1 ? " is" : "s are")} still hidden — use Show: hidden to bring them back."
                : $"Deleted \"{set.Name}\".";
            Recompose();
        }

        // ------------------------------------------------------------------ the set editor

        void OpenEditSet(string id)
        {
            var set = SetsService?.ById(id);
            if (set == null) return;
            editingSet = set.Copy();
            editingIsNew = false;
            screen = PmScreen.EditSet;
            Recompose();
        }

        void ComposeEditSet(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            var tip = CairoFont.WhiteDetailText();
            double y = 42;

            if (editingSet == null) { BackToMatrix(); return; }

            c.AddStaticText("Name", font, EB(4, y + 4, 60, 26));
            c.AddTextInput(EB(68, y, 240, 28), t => editingSet.Name = t ?? "", font, "setname");
            c.AddSwitch(on => editingSet.ShowButton = on, EB(330, y, 26, 26), "setshowbutton", 23, 3);
            c.AddStaticText("Show in the map panel", font, EB(362, y + 4, 240, 25));
            c.AddHoverText(
                "Untick to keep the set for use from this screen without giving it a row in the "
                + "pin-set panel on the world map.",
                tip, 320, EB(330, y, 272, 28).FlatCopy(), "tipsetbutton");
            y += 36;

            // The icon choice is what turns a named button into a symbol. Offered as a dropdown
            // rather than a grid of every icon for the same reason the filter bar stopped using one:
            // this is a choice made once per set, and it does not deserve a third of the screen.
            c.AddStaticText("Panel icon", font, EB(4, y + 4, 110, 26));
            var iconValues = ButtonIconValues();
            c.AddDropDown(iconValues, ButtonIconLabels(iconValues), Math.Max(0, Array.IndexOf(iconValues, editingSet.ButtonIcon ?? "")),
                OnEditSetButtonIconChanged, EB(118, y, 220, 28), "setbuttonicon");
            c.AddHoverText(
                "The icon shown against this set in the map's pin-set panel: in colour while any of "
                + "its pins are on the map, greyed out once they are all hidden. \"No icon\" gives a "
                + "plain colour chip instead, so the column still lines up. A set filtering on exactly "
                + "one colour is drawn in that colour; with none or several it stays white, since "
                + "there is no one colour that would be honest.",
                tip, 340, EB(4, y, 334, 28).FlatCopy(), "tipsetbuttonicon");
            y += 38;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 10;
            c.AddStaticText("Matches pins where...", CairoFont.WhiteSmallishText(), EB(4, y, 300, 26));
            y += 30;

            c.AddStaticText("Name contains", font, EB(4, y + 4, 130, 26));
            c.AddTextInput(EB(138, y, 240, 28), t => editingSet.Search = t ?? "", font, "setsearch");
            c.AddHoverText("Case-insensitive, and matches anywhere in the title. Leave empty to match any name.",
                tip, 300, EB(4, y, 374, 28).FlatCopy(), "tipsetsearch");

            c.AddSwitch(on => editingSet.PinnedOnly = on, EB(410, y, 26, 26), "setpinned", 23, 3);
            c.AddStaticText("Pinned only", font, EB(442, y + 4, 120, 25));
            y += 38;

            // The same two dropdowns as the filter bar, built from the same live values, so what a
            // set can express and what the table can filter on stay the same thing by construction.
            c.AddStaticText("Colours", font, EB(4, y + 4, 130, 26));
            c.AddMultiSelectDropDown(filterColorHexes, ColorFilterLabels(), -1, OnEditSetColorChanged, EB(138, y, 200, 28), "setcolors");
            c.AddStaticText("Icons", font, EB(354, y + 4, 60, 26));
            c.AddMultiSelectDropDown(filterIconCodes, IconFilterLabels(), -1, OnEditSetIconChanged, EB(416, y, 170, 28), "seticons");
            c.AddHoverText("Empty means \"any\". Both lists show only the colours and icons your pins actually use.",
                tip, 320, EB(354, y, 232, 28).FlatCopy(), "tipseticons");
            y += 42;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 12;

            c.AddDynamicText(EditSetPreview(), font, EB(4, y, DW - 8, 26), "setpreview");
            y += 32;
            c.AddDynamicText(EditSetWarning(), CairoFont.WhiteDetailText(), EB(4, y, DW - 8, 26), "setwarning");
            y += 34;

            c.AddSmallButton("Use the table's current filter", OnAdoptCurrentFilter, EB(4, y, 260, 28), EnumButtonStyle.Small);
            c.AddSmallButton("Cancel", () => { editingSet = null; OpenPinSets(); return true; }, EB(DW - 232, y, 100, 28));
            c.AddSmallButton("Save", OnSaveSet, EB(DW - 126, y, 122, 28));
        }

        /// <summary>
        /// The icon dropdown's values: "" for a plain colour chip, then every icon that can actually
        /// be painted. The full set rather than only icons in use — a row's icon is a label and need
        /// not be an icon any of its pins wear.
        /// </summary>
        string[] ButtonIconValues()
        {
            var list = new List<string> { "" };
            list.AddRange(DrawableIconCodes());
            return list.ToArray();
        }

        string[] ButtonIconLabels(string[] values) => values
            .Select(code => code.Length == 0
                ? "No icon (colour chip)"
                : $"<{IconGlyphComponent.TagName} code=\"{code}\"/> {code}")
            .ToArray();

        void OnEditSetButtonIconChanged(string code, bool selected)
        {
            if (editingSet == null) return;
            editingSet.ButtonIcon = code ?? "";
        }

        void RestoreEditSetState()
        {
            if (editingSet == null) return;
            var c = SingleComposer;
            c.GetTextInput("setname").SetValue(editingSet.Name ?? "");
            c.GetTextInput("setsearch").SetValue(editingSet.Search ?? "");
            c.GetSwitch("setpinned").SetValue(editingSet.PinnedOnly);
            c.GetSwitch("setshowbutton").SetValue(editingSet.ShowButton);
            if (editingSet.Colors.Count > 0) c.GetDropDown("setcolors").SetSelectedValue(editingSet.Colors.ToArray());
            if (editingSet.Icons.Count > 0) c.GetDropDown("seticons").SetSelectedValue(editingSet.Icons.ToArray());
        }

        void OnEditSetColorChanged(string hex, bool selected)
        {
            if (editingSet == null) return;
            if (selected) { if (!editingSet.Colors.Contains(hex)) editingSet.Colors.Add(hex); }
            else editingSet.Colors.Remove(hex);
            UpdateEditSetDynamic();
        }

        void OnEditSetIconChanged(string code, bool selected)
        {
            if (editingSet == null) return;
            if (selected) { if (!editingSet.Icons.Contains(code)) editingSet.Icons.Add(code); }
            else editingSet.Icons.Remove(code);
            UpdateEditSetDynamic();
        }

        void UpdateEditSetDynamic()
        {
            if (screen != PmScreen.EditSet || SingleComposer == null) return;
            SingleComposer.GetDynamicText("setpreview")?.SetNewText(EditSetPreview(), false, true, false);
            SingleComposer.GetDynamicText("setwarning")?.SetNewText(EditSetWarning(), false, true, false);
        }

        /// <summary>
        /// How many pins the criteria catch right now, counted against the live list rather than
        /// the filtered table. Editing a set is exactly the moment to find out that the filter you
        /// thought said "resin" actually says "resin AND blue AND pinned" and catches nothing.
        /// </summary>
        string EditSetPreview()
        {
            if (editingSet == null) return "";
            int n;
            try { n = svc.Own.Count(editingSet.Matches); }
            catch (Exception) { return ""; }
            return n == 0
                ? "Matches no pins right now."
                : $"Matches {n} pin{(n == 1 ? "" : "s")} right now.";
        }

        string EditSetWarning()
        {
            if (editingSet == null) return "";
            if (editingSet.MatchesEverything)
                return "No criteria set — this matches every pin you own, so switching it off hides all of them.";
            return "";
        }

        /// <summary>Overwrites the criteria from whatever the table is filtered by at this moment.</summary>
        bool OnAdoptCurrentFilter()
        {
            if (editingSet == null) return true;
            editingSet.Search = searchText;
            editingSet.Icons = iconFilter.ToList();
            editingSet.Colors = colorFilter.ToList();
            editingSet.PinnedOnly = pinnedOnly;
            Recompose();
            return true;
        }

        bool OnSaveSet()
        {
            if (editingSet == null) { OpenPinSets(); return true; }

            editingSet.Name = (editingSet.Name ?? "").Trim();
            if (editingSet.Name.Length == 0)
            {
                // The name IS the button, so an unnamed set cannot be saved - there would be
                // nothing to press and nothing to find it by on this screen.
                notice = "Give the set a name first — it is what the panel row says.";
                UpdateEditSetDynamic();
                SingleComposer?.GetDynamicText("setwarning")?.SetNewText(notice, false, true, false);
                return true;
            }

            if (editingIsNew) SetsService?.Add(editingSet);
            else SetsService?.Update(editingSet);

            string name = editingSet.Name;
            editingSet = null;
            ModSystem?.RefreshSetPanel();
            notice = $"Saved \"{name}\".";
            OpenPinSets();
            return true;
        }

        // ------------------------------------------------------------------ tools

        /// <summary>
        /// Everything that used to sit on the matrix screen's action rows and is pressed rarely.
        /// Grouped by what it does to your data, loudest first, because that is the order in which
        /// pressing the wrong one matters.
        /// </summary>
        void ComposeTools(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            var head = CairoFont.WhiteSmallishText();
            var tip = CairoFont.WhiteDetailText();
            double y = 42;

            c.AddStaticText("Clean up", head, EB(4, y, 300, 26));
            y += 30;

            int dupes = DuplicateCount();
            c.AddSmallButton(dupes > 0 ? $"Fix duplicates ({dupes})..." : "Fix duplicates...",
                () => { BuildFixDuplicates(); return true; }, EB(4, y, 190, 28));
            c.AddHoverText("Pins identical in every column — the safe one. Shows a preview before anything is deleted.",
                tip, 320, EB(4, y, 190, 28).FlatCopy(), "tiptooldupes");
            y += 32;

            int spots = SameSpotCount();
            c.AddSmallButton(spots > 0 ? $"Fix same-spot pins ({spots})..." : "Fix same-spot pins...",
                () => { BuildFixSameSpot(); return true; }, EB(4, y, 190, 28));
            c.AddHoverText("Pins in one place under different names. Unlike Fix duplicates this can delete pins "
                + "that genuinely differ, so it previews the survivor of every set, not just the condemned.",
                tip, 320, EB(4, y, 190, 28).FlatCopy(), "tiptoolspots");
            y += 38;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 12;
            c.AddStaticText("Data", head, EB(4, y, 300, 26));
            y += 30;

            c.AddSmallButton($"Recycle bin ({bin.Entries.Count})...", () => { OpenBin(); return true; }, EB(4, y, 190, 28));
            c.AddHoverText("Restore pins deleted by a bulk operation.", tip, 300, EB(4, y, 190, 28).FlatCopy(), "tiptoolbin");
            y += 32;

            c.AddSmallButton("Export / Import...", () => { screen = PmScreen.ImportExport; Recompose(); return true; }, EB(4, y, 190, 28));
            c.AddHoverText("Write your pins to a file, or read a file back in.", tip, 300, EB(4, y, 190, 28).FlatCopy(), "tiptoolport");
            y += 38;

            c.AddInset(EB(4, y, DW - 8, 2), 2);
            y += 12;
            c.AddStaticText("Refresh", head, EB(4, y, 300, 26));
            y += 30;

            c.AddSmallButton("Refresh", OnRefreshClicked, EB(4, y, 190, 28));
            c.AddHoverText("Re-read the waypoint list from the game.", tip, 300, EB(4, y, 190, 28).FlatCopy(), "tiptoolrefresh");
            y += 32;

            if (config.EnableMapRefresh)
            {
                c.AddSmallButton("Redraw map", () => { ExecuteMapRedraw(); return true; }, EB(4, y, 190, 28));
                c.AddHoverText("Runs the game's own \".map redraw\".", tip, 300, EB(4, y, 190, 28).FlatCopy(), "tiptoolredraw");
                y += 32;
            }

            y += 8;
            c.AddSmallButton("Back", () => { BackToMatrix(); return true; }, EB(DW - 100, y, 96, 28));
        }
    }
}
