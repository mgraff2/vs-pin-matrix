using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace PinMatrix
{
    public partial class GuiDialogPinMatrix
    {
        // ------------------------------------------------------------------ batch plumbing

        void RunBatch(List<string> cmds, string doneMsg, PmScreen doneScreen = PmScreen.Matrix, Action beforeRefresh = null)
        {
            pending = null;
            screen = PmScreen.Matrix;
            notice = cmds.Count > 0 ? $"Executing {cmds.Count} commands..." : "";
            Recompose();

            batch.OnProgress = (done, total) =>
            {
                notice = $"Executing... {done}/{total}";
                UpdateMatrixDynamic();
            };

            batch.Run(cmds, () =>
            {
                beforeRefresh?.Invoke();
                notice = doneMsg;
                RefreshData();
                screen = doneScreen;
                Recompose();
            });
        }

        void MaybeAutoBackup()
        {
            if (!config.AutoBackupBeforeBulkOps) return;
            try
            {
                Porter.Export(capi, svc, svc.Own, "backup");
                var files = Directory.GetFiles(Porter.Folder(capi), "backup-*.json").OrderByDescending(f => f).ToList();
                foreach (var old in files.Skip(config.BackupRetentionCount)) File.Delete(old);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[pinmatrix] Auto-backup failed: {0}", e.Message);
            }
        }

        bool GuardSelection(out List<PinRow> rows)
        {
            rows = SelectedAllRows();
            if (batch.Busy)
            {
                notice = "A bulk operation is still running...";
                UpdateMatrixDynamic();
                return false;
            }
            if (rows.Count == 0)
            {
                notice = "Nothing selected — tick some rows first (filters + 'Select all filtered' is the fast way).";
                UpdateMatrixDynamic();
                return false;
            }
            return true;
        }

        void ShowConfirm(PendingBulk p)
        {
            pending = p;
            confPage = 0;
            screen = PmScreen.Confirm;
            Recompose();
        }

        // ------------------------------------------------------------------ bulk op: delete

        void BuildDelete()
        {
            if (!GuardSelection(out var rows)) return;
            var keys = rows.Select(r => r.Key).ToList();

            ShowConfirm(new PendingBulk
            {
                Title = $"Delete {rows.Count} pins? They will be moved to the recycle bin.",
                ConfirmText = $"Delete {rows.Count} pins",
                Lines = rows.Select(r => Disp(r.Wp) + "  ->  recycle bin").ToArray(),
                Execute = () => ExecDelete(keys, "Bulk delete", "Deleted"),
            });
        }

        /// <summary>
        /// CRITICAL (spec §4): resolve indices at execution time and delete in strictly descending
        /// order — every removal shifts the index of everything after it.
        /// </summary>
        void ExecDelete(List<string> keys, string binReason, string doneVerb)
        {
            MaybeAutoBackup();
            var resolved = new List<(int Index, Waypoint Wp)>();
            foreach (var key in keys)
            {
                int i = svc.ResolveIndex(key);
                if (i >= 0) resolved.Add((i, svc.Own[i]));
            }
            int missing = keys.Count - resolved.Count;
            bin.Add(resolved.Select(t => t.Wp), binReason);
            var cmds = resolved.OrderByDescending(t => t.Index).Select(t => WpCommands.Remove(t.Index)).ToList();
            undo = null;
            RunBatch(cmds, $"{doneVerb} {resolved.Count} pins — they are in the recycle bin."
                + (missing > 0 ? $" ({missing} were already gone.)" : ""));
        }

        /// <summary>
        /// Deletes every copy but one from each set of pins identical in all columns. Deliberately
        /// scans the whole waypoint list rather than the current view — a filter hiding part of a
        /// duplicate set would otherwise leave copies behind while reporting the job done. The kept
        /// copy is the earliest one in the layer's own index order, i.e. the original — except that
        /// a copy still drawn on the map always beats a hidden one, so cleaning up duplicates can
        /// never leave the survivor invisible.
        /// </summary>
        /// <summary>The copy of a duplicate set that survives: the first visible one, else the first.</summary>
        PinRow KeptCopy(DupGroup g) => g.Rows.FirstOrDefault(r => !IsHidden(r)) ?? g.Rows[0];

        void BuildFixDuplicates()
        {
            if (batch.Busy) { notice = "A bulk operation is still running..."; UpdateMatrixDynamic(); return; }

            var dupSets = GroupByDuplicate(allRows).Where(g => g.Rows.Count > 1).ToList();
            var doomed = dupSets.SelectMany(g => g.Rows.Where(r => r != KeptCopy(g))).ToList();
            if (doomed.Count == 0)
            {
                notice = "No duplicate pins found — every pin differs from the others in at least one column.";
                UpdateMatrixDynamic();
                return;
            }

            var keys = doomed.Select(r => r.Key).ToList();
            ShowConfirm(new PendingBulk
            {
                Title = $"Delete {doomed.Count} duplicate pins across {dupSets.Count} sets, keeping the original of each?",
                Warning = "Pins are duplicates only when title, icon, color, pinned state and position all match.",
                ConfirmText = $"Delete {doomed.Count} duplicates",
                Lines = doomed.Select(r => Disp(r.Wp) + "  ->  recycle bin").ToArray(),
                Execute = () => ExecDelete(keys, "Duplicate cleanup", "Removed"),
            });
        }

        // ------------------------------------------------------------------ bulk op: modify family

        void ExecModify(List<(string Key, int Color, string Icon, bool Pinned, string Title)> targets, string undoLabel, string doneMsg)
        {
            MaybeAutoBackup();
            var undoState = new UndoState { Label = undoLabel };
            var cmds = new List<string>();
            foreach (var t in targets)
            {
                int i = svc.ResolveIndex(t.Key);
                if (i < 0) continue;
                var wp = svc.Own[i];
                undoState.Entries.Add(new UndoEntry { Key = t.Key, Title = wp.Title, Color = wp.Color, Icon = wp.Icon, Pinned = wp.Pinned });
                cmds.Add(WpCommands.Modify(i, t.Color, t.Icon, t.Pinned, t.Title));
            }
            if (cmds.Count > 0) undo = undoState;
            RunBatch(cmds, doneMsg);
        }

        void BuildSetColor(int color)
        {
            if (!GuardSelection(out var rows)) return;
            var changed = rows.Where(r => (r.Wp.Color & 0xFFFFFF) != (color & 0xFFFFFF)).ToList();
            if (changed.Count == 0)
            {
                notice = "All selected pins already have that color.";
                BackToMatrix();
                return;
            }
            var targets = changed.Select(r => (r.Key, color, WpCommands.SafeIcon(r.Wp.Icon), r.Wp.Pinned, WpCommands.SafeTitle(r.Wp.Title))).ToList();

            ShowConfirm(new PendingBulk
            {
                Title = $"Recolor {changed.Count} pins to {WpCommands.ColorHex(color)}?",
                ConfirmText = $"Recolor {changed.Count} pins",
                Lines = changed.Select(r => $"{Disp(r.Wp)}: {WpCommands.ColorHex(r.Wp.Color)} -> {WpCommands.ColorHex(color)}").ToArray(),
                Execute = () => ExecModify(targets, $"Recolor {changed.Count} pins", $"Recolored {changed.Count} pins."),
            });
        }

        void BuildSetIcon(string icon)
        {
            if (!GuardSelection(out var rows)) return;
            icon = WpCommands.SafeIcon(icon);
            var changed = rows.Where(r => WpCommands.SafeIcon(r.Wp.Icon) != icon).ToList();
            if (changed.Count == 0)
            {
                notice = "All selected pins already have that icon.";
                BackToMatrix();
                return;
            }
            var targets = changed.Select(r => (r.Key, r.Wp.Color, icon, r.Wp.Pinned, WpCommands.SafeTitle(r.Wp.Title))).ToList();

            ShowConfirm(new PendingBulk
            {
                Title = $"Change icon of {changed.Count} pins to '{icon}'?",
                ConfirmText = $"Re-icon {changed.Count} pins",
                Lines = changed.Select(r => $"{Disp(r.Wp)}: {WpCommands.SafeIcon(r.Wp.Icon)} -> {icon}").ToArray(),
                Execute = () => ExecModify(targets, $"Re-icon {changed.Count} pins", $"Changed icon on {changed.Count} pins."),
            });
        }

        void BuildPin(bool pin)
        {
            if (!GuardSelection(out var rows)) return;
            var changed = rows.Where(r => r.Wp.Pinned != pin).ToList();
            if (changed.Count == 0)
            {
                notice = pin ? "All selected pins are already pinned." : "None of the selected pins are pinned.";
                UpdateMatrixDynamic();
                return;
            }
            var targets = changed.Select(r => (r.Key, r.Wp.Color, WpCommands.SafeIcon(r.Wp.Icon), pin, WpCommands.SafeTitle(r.Wp.Title))).ToList();

            string warning = null;
            if (pin)
            {
                int resulting = allRows.Count(r => r.Wp.Pinned && !selectedKeys.Contains(r.Key)) + rows.Count;
                if (resulting > config.PinnedWarnThreshold)
                {
                    warning = $"Warning: this leaves {resulting} pins locked to the screen edge — the map HUD will be very crowded.";
                }
            }

            string verb = pin ? "Pin" : "Unpin";
            ShowConfirm(new PendingBulk
            {
                Title = $"{verb} {changed.Count} pins?",
                Warning = warning,
                ConfirmText = $"{verb} {changed.Count} pins",
                Lines = changed.Select(r => $"{Disp(r.Wp)}: {(r.Wp.Pinned ? "pinned" : "unpinned")} -> {(pin ? "pinned" : "unpinned")}").ToArray(),
                Execute = () => ExecModify(targets, $"{verb} {changed.Count} pins", $"{verb}ned {changed.Count} pins."),
            });
        }

        // ------------------------------------------------------------------ rename

        string findText = "", replaceText = "", prefixText = "", suffixText = "";

        void OpenRename()
        {
            if (!GuardSelection(out _)) return;
            screen = PmScreen.Rename;
            Recompose();
        }

        void ComposeRename(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            double y = 42;
            c.AddStaticText($"Rename {selectedKeys.Count} selected pins. Find & replace is plain text, case-sensitive; prefix/suffix are added around the result.", font, EB(4, y, DW, 44));
            y += 50;
            c.AddStaticText("Find", font, EB(4, y + 4, 90, 25));
            c.AddTextInput(EB(100, y, 280, 28), t => findText = t, font, "find");
            c.AddStaticText("Replace with", font, EB(400, y + 4, 100, 25));
            c.AddTextInput(EB(504, y, 280, 28), t => replaceText = t, font, "replace");
            y += 38;
            c.AddStaticText("Add prefix", font, EB(4, y + 4, 90, 25));
            c.AddTextInput(EB(100, y, 280, 28), t => prefixText = t, font, "prefix");
            c.AddStaticText("Add suffix", font, EB(400, y + 4, 100, 25));
            c.AddTextInput(EB(504, y, 280, 28), t => suffixText = t, font, "suffix");
            y += 48;
            c.AddSmallButton("Preview changes", () => { BuildRename(); return true; }, EB(4, y, 150, 30));
            c.AddSmallButton("Cancel", () => { BackToMatrix(); return true; }, EB(160, y, 90, 30));
        }

        void RestoreRenameState()
        {
            SingleComposer.GetTextInput("find").SetValue(findText);
            SingleComposer.GetTextInput("replace").SetValue(replaceText);
            SingleComposer.GetTextInput("prefix").SetValue(prefixText);
            SingleComposer.GetTextInput("suffix").SetValue(suffixText);
        }

        void BuildRename()
        {
            if (!GuardSelection(out var rows)) return;

            string Transform(string title)
            {
                title = title ?? "";
                if (findText.Length > 0) title = title.Replace(findText, replaceText ?? "");
                return prefixText + title + suffixText;
            }

            var changed = rows
                .Select(r => (Row: r, After: Transform(r.Wp.Title)))
                .Where(t => t.After != (t.Row.Wp.Title ?? "") && WpCommands.SafeTitle(t.After) != WpCommands.SafeTitle(t.Row.Wp.Title))
                .ToList();

            if (changed.Count == 0)
            {
                notice = "Rename rule matches no selected pins (nothing would change).";
                BackToMatrix();
                return;
            }

            var targets = changed.Select(t => (t.Row.Key, t.Row.Wp.Color, WpCommands.SafeIcon(t.Row.Wp.Icon), t.Row.Wp.Pinned, WpCommands.SafeTitle(t.After))).ToList();

            ShowConfirm(new PendingBulk
            {
                Title = $"Rename {changed.Count} pins?",
                ConfirmText = $"Rename {changed.Count} pins",
                Lines = changed.Select(t => $"'{t.Row.Wp.Title}' -> '{WpCommands.SafeTitle(t.After)}'").ToArray(),
                Execute = () => ExecModify(targets, $"Rename {changed.Count} pins", $"Renamed {changed.Count} pins."),
            });
        }

        // ------------------------------------------------------------------ undo

        void BuildUndo()
        {
            if (undo == null || undo.Entries.Count == 0)
            {
                notice = "Nothing to undo (only the most recent bulk edit can be undone; deletes are restored from the bin).";
                UpdateMatrixDynamic();
                return;
            }
            var entries = undo.Entries;
            var targets = entries.Select(e => (e.Key, e.Color, WpCommands.SafeIcon(e.Icon), e.Pinned, WpCommands.SafeTitle(e.Title))).ToList();

            ShowConfirm(new PendingBulk
            {
                Title = $"Undo \"{undo.Label}\" ({entries.Count} pins)?",
                ConfirmText = $"Undo on {entries.Count} pins",
                Lines = entries.Select(e => $"{WpCommands.SafeTitle(e.Title)}: restore previous name/color/icon/pin state").ToArray(),
                Execute = () => ExecModify(targets, $"Undo of: {undo.Label}", "Undid last bulk operation."),
            });
        }

        // ------------------------------------------------------------------ set color / set icon screens

        int pickerColorIdx = -1;
        string pickerHex = "";
        int pickerIconIdx = -1;

        void OpenSetColor()
        {
            if (!GuardSelection(out _)) return;
            pickerColorIdx = -1;
            pickerHex = "";
            screen = PmScreen.SetColor;
            Recompose();
        }

        void ComposeSetColor(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            var colors = svc.PaletteColors();
            double y = 42;
            c.AddStaticText($"Pick a color for the {selectedKeys.Count} selected pins:", font, EB(4, y, DW, 25));
            y += 30;
            c.AddColorListPicker(colors, i => { pickerColorIdx = i; }, EB(4, y, 27, 27), 750, "colorpicker");
            y += 40 * (int)Math.Ceiling(colors.Length * 31.0 / 750) + 20;
            c.AddStaticText("...or hex", font, EB(4, y + 4, 70, 25));
            c.AddTextInput(EB(80, y, 130, 28), t => pickerHex = t, font, "hex");
            c.AddStaticText("(e.g. #ff8800 — overrides the palette choice)", font, EB(220, y + 4, 420, 25));
            y += 44;
            c.AddSmallButton("Preview changes", () => { OnSetColorNext(); return true; }, EB(4, y, 150, 30));
            c.AddSmallButton("Cancel", () => { BackToMatrix(); return true; }, EB(160, y, 90, 30));
        }

        void OnSetColorNext()
        {
            int color;
            string hex = pickerHex.Trim().TrimStart('#');
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                color = unchecked((int)0xFF000000) | rgb;
            }
            else if (pickerColorIdx >= 0 && pickerColorIdx < svc.PaletteColors().Length)
            {
                color = svc.PaletteColors()[pickerColorIdx];
            }
            else
            {
                notice = "Pick a palette color or enter a #rrggbb hex value first.";
                BackToMatrix();
                return;
            }
            BuildSetColor(color);
        }

        void OpenSetIcon()
        {
            if (!GuardSelection(out _)) return;
            pickerIconIdx = -1;
            screen = PmScreen.SetIcon;
            Recompose();
        }

        void ComposeSetIcon(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            var icons = DrawableIconCodes();
            double y = 42;
            c.AddStaticText($"Pick an icon for the {selectedKeys.Count} selected pins:", font, EB(4, y, DW, 25));
            y += 30;
            c.AddIconListPicker(icons, i => { pickerIconIdx = i; }, EB(4, y, 27, 27), 750, "iconpicker");
            y += 40 * (int)Math.Ceiling(icons.Length * 31.0 / 750) + 20;
            c.AddSmallButton("Preview changes", () =>
            {
                var iconsNow = DrawableIconCodes();
                if (pickerIconIdx < 0 || pickerIconIdx >= iconsNow.Length)
                {
                    notice = "Pick an icon first.";
                    BackToMatrix();
                    return true;
                }
                BuildSetIcon(iconsNow[pickerIconIdx]);
                return true;
            }, EB(4, y, 150, 30));
            c.AddSmallButton("Cancel", () => { BackToMatrix(); return true; }, EB(160, y, 90, 30));
        }

        // ------------------------------------------------------------------ confirm screen

        ElementBounds confTableBounds;
        const int ConfPageSize = 16;

        void ComposeConfirm(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            double y = 42;

            c.AddStaticText(pending.Title, CairoFont.WhiteSmallishText(), EB(4, y, DW, 28));
            y += 32;
            if (pending.Warning != null)
            {
                c.AddStaticText(pending.Warning, font.Clone().WithColor(GuiStyle.ErrorTextColor), EB(4, y, DW, 42));
                y += 46;
            }

            double tableH = ConfPageSize * ConfRowH;
            c.AddInset(EB(2, y - 2, DW + 4, tableH + 4), 3);
            confTableBounds = EB(4, y, DW, tableH);
            c.AddDynamicCustomDraw(confTableBounds, DrawConfirmList, "conflist");
            y += tableH + 8;

            int maxConfPage = Math.Max(0, (pending.Lines.Length - 1) / ConfPageSize);
            c.AddSmallButton("< Prev", () => { if (confPage > 0) { confPage--; RedrawConfirm(); } return true; }, EB(4, y, 78, 26));
            c.AddDynamicText($"Page {confPage + 1}/{maxConfPage + 1}", font.Clone().WithOrientation(EnumTextOrientation.Center), EB(86, y + 4, 110, 24), "confpage");
            c.AddSmallButton("Next >", () => { if (confPage < maxConfPage) { confPage++; RedrawConfirm(); } return true; }, EB(200, y, 78, 26));

            y += 36;
            c.AddSmallButton("Cancel", () =>
            {
                var back = pending?.ReturnScreen ?? PmScreen.Matrix;
                pending = null;
                screen = back;
                Recompose();
                return true;
            }, EB(4, y, 100, 32));
            c.AddSmallButton(pending.ConfirmText, () =>
            {
                var p = pending;
                pending = null;
                if (p != null && !batch.Busy) p.Execute();
                return true;
            }, EB(112, y, 320, 32));
        }

        void RedrawConfirm()
        {
            (SingleComposer.GetElement("conflist") as GuiElementCustomDraw)?.Redraw();
            int maxConfPage = pending == null ? 0 : Math.Max(0, (pending.Lines.Length - 1) / ConfPageSize);
            SingleComposer.GetDynamicText("confpage")?.SetNewText($"Page {confPage + 1}/{maxConfPage + 1}", false, true, false);
        }

        void DrawConfirmList(Context ctx, ImageSurface surface, ElementBounds bounds)
        {
            if (pending == null) return;
            var font = CairoFont.WhiteSmallText();
            double rh = GuiElement.scaled(ConfRowH);
            int start = confPage * ConfPageSize;
            int count = Math.Min(ConfPageSize, Math.Max(0, pending.Lines.Length - start));
            for (int i = 0; i < count; i++)
            {
                if ((start + i) % 2 == 0)
                {
                    ctx.SetSourceRGBA(1, 1, 1, 0.045);
                    ctx.Rectangle(0, i * rh, GuiElement.scaled(DW), rh);
                    ctx.Fill();
                }
                string t = Trunc(font, pending.Lines[start + i], GuiElement.scaled(DW - 16));
                font.SetupContext(ctx);
                capi.Gui.Text.DrawTextLine(ctx, font, t, GuiElement.scaled(8), i * rh + GuiElement.scaled(2), false);
            }
        }

        // ------------------------------------------------------------------ new pin / move screen

        bool newPinIsMove;
        string movePinKey;
        string npIcon = "circle";
        int npColorIdx = -1;
        string npHex = "";

        void OpenNewPin()
        {
            newPinIsMove = false;
            movePinKey = null;
            npIcon = "circle";
            npColorIdx = -1;
            npHex = "";
            screen = PmScreen.NewPin;
            Recompose();
            PrefillCoordsFromPlayer();
        }

        void OpenMove(PinRow row)
        {
            newPinIsMove = true;
            movePinKey = row.Key;
            npIcon = WpCommands.SafeIcon(row.Wp.Icon);
            npColorIdx = -1;
            npHex = WpCommands.ColorHex(row.Wp.Color);
            screen = PmScreen.NewPin;
            Recompose();
            var c = SingleComposer;
            c.GetTextInput("npname").SetValue(row.Wp.Title ?? "");
            c.GetNumberInput("npx").SetValue(FmtCoord(svc.RelX(row.Wp.Position.X)));
            c.GetNumberInput("npy").SetValue(FmtCoord(row.Wp.Position.Y));
            c.GetNumberInput("npz").SetValue(FmtCoord(svc.RelZ(row.Wp.Position.Z)));
            c.GetSwitch("nppinned").SetValue(row.Wp.Pinned);
        }

        void PrefillCoordsFromPlayer()
        {
            var pos = capi.World.Player.Entity.Pos;
            var c = SingleComposer;
            c.GetNumberInput("npx").SetValue(FmtCoord(svc.RelX(pos.X)));
            c.GetNumberInput("npy").SetValue(FmtCoord(pos.Y));
            c.GetNumberInput("npz").SetValue(FmtCoord(svc.RelZ(pos.Z)));
        }

        void ComposeNewPin(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            var icons = DrawableIconCodes();
            var colors = svc.PaletteColors();
            double y = 42;

            c.AddStaticText("Name", font, EB(4, y + 4, 60, 25));
            c.AddTextInput(EB(70, y, 300, 28), _ => { }, font, "npname");
            c.AddSwitch(on => { }, EB(400, y, 28, 28), "nppinned", 25, 3);
            c.AddStaticText("Pinned", font, EB(433, y + 4, 60, 25));
            y += 38;

            c.AddStaticText("Icon:", font, EB(4, y + 2, 50, 25));
            y += 26;
            c.AddIconListPicker(icons, i => { npIcon = icons[i]; }, EB(4, y, 27, 27), 750, "npicons");
            y += 40 * (int)Math.Ceiling(icons.Length * 31.0 / 750) + 8;

            c.AddStaticText("Coords (X/Z relative to world spawn, Y absolute — same as the coordinate HUD):", font, EB(4, y, DW, 25));
            y += 28;
            c.AddStaticText("X", font, EB(4, y + 4, 20, 25));
            c.AddNumberInput(EB(26, y, 100, 28), _ => { }, font, "npx");
            c.AddStaticText("Y", font, EB(140, y + 4, 20, 25));
            c.AddNumberInput(EB(162, y, 100, 28), _ => { }, font, "npy");
            c.AddStaticText("Z", font, EB(276, y + 4, 20, 25));
            c.AddNumberInput(EB(298, y, 100, 28), _ => { }, font, "npz");
            c.AddSmallButton("Set to my position", () => { PrefillCoordsFromPlayer(); return true; }, EB(412, y, 160, 28));
            y += 40;

            c.AddStaticText("Color:", font, EB(4, y + 4, 50, 25));
            y += 28;
            c.AddColorListPicker(colors, i => { npColorIdx = i; npHex = ""; SingleComposer?.GetTextInput("nphex")?.SetValue(""); }, EB(4, y, 27, 27), 750, "npcolors");
            y += 40 * (int)Math.Ceiling(colors.Length * 31.0 / 750) + 16;
            c.AddStaticText("...or hex", font, EB(4, y + 4, 70, 25));
            c.AddTextInput(EB(80, y, 130, 28), t => npHex = t, font, "nphex");
            y += 44;

            c.AddSmallButton(newPinIsMove ? "Preview move" : "Create pin", () => { OnNewPinSave(); return true; }, EB(4, y, 150, 32));
            c.AddSmallButton("Cancel", () => { BackToMatrix(); return true; }, EB(160, y, 90, 32));
        }

        void RestoreNewPinState()
        {
            if (npHex.Length > 0) SingleComposer.GetTextInput("nphex").SetValue(npHex);
            int iconIdx = Array.IndexOf(DrawableIconCodes(), npIcon);   // same list the picker was built from
            if (iconIdx >= 0) SingleComposer.IconListPickerSetValue("npicons", iconIdx);
        }

        void OnNewPinSave()
        {
            if (batch.Busy) { notice = "A bulk operation is still running..."; return; }

            var c = SingleComposer;
            string name = WpCommands.SafeTitle(c.GetTextInput("npname").GetText());
            double relX = c.GetNumberInput("npx").GetValue();
            double absY = svc.ClampY(c.GetNumberInput("npy").GetValue());
            double relZ = c.GetNumberInput("npz").GetValue();
            double absX = svc.ClampAbsX(svc.AbsX(relX));
            double absZ = svc.ClampAbsZ(svc.AbsZ(relZ));
            bool pinned = c.GetSwitch("nppinned").On;

            int color;
            string hex = npHex.Trim().TrimStart('#');
            var palette = svc.PaletteColors();
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) color = unchecked((int)0xFF000000) | rgb;
            else if (npColorIdx >= 0 && npColorIdx < palette.Length) color = palette[npColorIdx];
            else color = palette[0];

            if (!newPinIsMove)
            {
                // creation is non-destructive: commits directly, no confirmation page (spec §2)
                RunBatch(new List<string> { WpCommands.Add(npIcon, absX, absY, absZ, pinned, color, name) }, $"Created pin '{name}'.");
                return;
            }

            string key = movePinKey;
            ShowConfirm(new PendingBulk
            {
                Title = $"Move '{name}'? (The pin is re-created at the new position; the vanilla waypoint list has no move command.)",
                ConfirmText = "Move pin",
                Lines = new[] { $"{name} -> ({FmtCoord(relX)}, {FmtCoord(absY)}, {FmtCoord(relZ)})" },
                Execute = () =>
                {
                    int i = svc.ResolveIndex(key);
                    if (i < 0)
                    {
                        notice = "That pin no longer exists.";
                        RefreshData();
                        BackToMatrix();
                        return;
                    }
                    // add first (append shifts nothing), then remove the old index
                    var cmds = new List<string>
                    {
                        WpCommands.Add(npIcon, absX, absY, absZ, pinned, color, name),
                        WpCommands.Remove(i)
                    };
                    undo = null;
                    RunBatch(cmds, $"Moved '{name}'.");
                }
            });
        }

        // ------------------------------------------------------------------ recycle bin screen

        ElementBounds binTableBounds;
        List<BinEntry> binView = new List<BinEntry>();
        readonly HashSet<BinEntry> binSelected = new HashSet<BinEntry>();
        int binPage;

        void OpenBin()
        {
            binSelected.Clear();
            binPage = 0;
            screen = PmScreen.Bin;
            Recompose();
        }

        void ComposeBin(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            binView = bin.Entries.Reverse().ToList();   // newest first
            binSelected.IntersectWith(binView);
            double y = 42;

            c.AddSmallButton("Restore selected", () => { BuildRestore(binSelected.ToList()); return true; }, EB(4, y, 150, 28));
            c.AddSmallButton("Restore all", () => { BuildRestore(binView.ToList()); return true; }, EB(160, y, 110, 28));
            c.AddSmallButton("Empty bin...", () => { BuildEmptyBin(); return true; }, EB(276, y, 120, 28));
            c.AddSmallButton("Back", () => { BackToMatrix(); return true; }, EB(402, y, 80, 28));
            c.AddDynamicText($"{binView.Count} binned · {binSelected.Count} selected", font.Clone().WithOrientation(EnumTextOrientation.Right), EB(490, y + 4, 360, 24), "binstatus");

            y += 36;
            double tableH = PageSize * RowH;
            c.AddInset(EB(2, y - 2, DW + 4, tableH + 4), 3);
            binTableBounds = EB(4, y, DW, tableH);
            c.AddDynamicCustomDraw(binTableBounds, DrawBinTable, "bintable");

            y += tableH + 8;
            int maxBinPage = Math.Max(0, (binView.Count - 1) / PageSize);
            c.AddSmallButton("< Prev", () => { if (binPage > 0) { binPage--; UpdateBinDynamic(); } return true; }, EB(4, y, 78, 26));
            c.AddDynamicText($"Page {binPage + 1}/{maxBinPage + 1}", font.Clone().WithOrientation(EnumTextOrientation.Center), EB(86, y + 4, 110, 24), "binpage");
            c.AddSmallButton("Next >", () => { if (binPage < maxBinPage) { binPage++; UpdateBinDynamic(); } return true; }, EB(200, y, 78, 26));
            c.AddStaticText("Click rows to select. Restored pins are re-created as new waypoints.", font, EB(290, y + 4, DW - 290, 24));
        }

        void UpdateBinDynamic()
        {
            (SingleComposer.GetElement("bintable") as GuiElementCustomDraw)?.Redraw();
            int maxBinPage = Math.Max(0, (binView.Count - 1) / PageSize);
            SingleComposer.GetDynamicText("binstatus")?.SetNewText($"{binView.Count} binned · {binSelected.Count} selected", false, true, false);
            SingleComposer.GetDynamicText("binpage")?.SetNewText($"Page {binPage + 1}/{maxBinPage + 1}", false, true, false);
        }

        void DrawBinTable(Context ctx, ImageSurface surface, ElementBounds bounds)
        {
            var font = CairoFont.WhiteSmallText();
            double rh = GuiElement.scaled(RowH);
            int start = binPage * PageSize;
            int count = Math.Min(PageSize, Math.Max(0, binView.Count - start));

            if (count == 0)
            {
                font.SetupContext(ctx);
                capi.Gui.Text.DrawTextLine(ctx, font, "The recycle bin is empty.", GuiElement.scaled(10), GuiElement.scaled(8), false);
                return;
            }

            for (int i = 0; i < count; i++)
            {
                var e = binView[start + i];
                double ry = i * rh;
                bool isSel = binSelected.Contains(e);

                if (isSel) { ctx.SetSourceRGBA(0.45, 0.62, 0.3, 0.32); ctx.Rectangle(0, ry, GuiElement.scaled(DW), rh); ctx.Fill(); }
                else if ((start + i) % 2 == 0) { ctx.SetSourceRGBA(1, 1, 1, 0.045); ctx.Rectangle(0, ry, GuiElement.scaled(DW), rh); ctx.Fill(); }

                double cb = GuiElement.scaled(15);
                double cbx = GuiElement.scaled(9);
                double cby = ry + (rh - cb) / 2;
                ctx.SetSourceRGBA(0.85, 0.85, 0.85, 0.85);
                ctx.LineWidth = 1.5;
                ctx.Rectangle(cbx, cby, cb, cb);
                ctx.Stroke();
                if (isSel)
                {
                    ctx.SetSourceRGBA(0.62, 0.85, 0.4, 1);
                    ctx.Rectangle(cbx + 3, cby + 3, cb - 6, cb - 6);
                    ctx.Fill();
                }

                DrawCell(ctx, font, e.Title ?? "", 34, 220, ry, rh);
                DrawIconGlyph(ctx, WpCommands.SafeIcon(e.Icon), GuiElement.scaled(280), ry + GuiElement.scaled(2.5), GuiElement.scaled(20), IconWhite);

                int col = e.Color;
                ctx.SetSourceRGBA(((col >> 16) & 0xff) / 255.0, ((col >> 8) & 0xff) / 255.0, (col & 0xff) / 255.0, 1);
                ctx.Rectangle(GuiElement.scaled(330), ry + GuiElement.scaled(5), GuiElement.scaled(38), rh - GuiElement.scaled(10));
                ctx.Fill();

                DrawCell(ctx, font, $"({FmtCoord(svc.RelX(e.X))}, {FmtCoord(e.Y)}, {FmtCoord(svc.RelZ(e.Z))})", 378, 175, ry, rh);
                DrawCell(ctx, font, e.DeletedAtUtc ?? "", 558, 150, ry, rh);
                DrawCell(ctx, font, e.Op ?? "", 712, 134, ry, rh);
            }
        }

        void HandleBinClick(MouseEvent args)
        {
            int rowOnPage = (int)((args.Y - binTableBounds.absY) / GuiElement.scaled(RowH));
            int idx = binPage * PageSize + rowOnPage;
            if (rowOnPage < 0 || rowOnPage >= PageSize || idx >= binView.Count) { args.Handled = true; return; }
            var e = binView[idx];
            if (!binSelected.Add(e)) binSelected.Remove(e);
            UpdateBinDynamic();
            args.Handled = true;
        }

        void BuildRestore(List<BinEntry> entries)
        {
            if (batch.Busy) { notice = "A bulk operation is still running..."; return; }
            if (entries.Count == 0)
            {
                SingleComposer.GetDynamicText("binstatus")?.SetNewText("Select some binned pins first.", false, true, false);
                return;
            }

            ShowConfirm(new PendingBulk
            {
                Title = $"Restore {entries.Count} pins from the recycle bin?",
                ConfirmText = $"Restore {entries.Count} pins",
                ReturnScreen = PmScreen.Bin,
                Lines = entries.Select(e => $"{WpCommands.SafeTitle(e.Title)} ({FmtCoord(svc.RelX(e.X))}, {FmtCoord(e.Y)}, {FmtCoord(svc.RelZ(e.Z))})").ToArray(),
                Execute = () =>
                {
                    var cmds = entries.Select(e => WpCommands.Add(e.Icon, e.X, e.Y, e.Z, e.Pinned, e.Color, e.Title)).ToList();
                    RunBatch(cmds, $"Restored {entries.Count} pins.", PmScreen.Bin, beforeRefresh: () =>
                    {
                        bin.Remove(entries);
                        binSelected.Clear();
                    });
                }
            });
        }

        void BuildEmptyBin()
        {
            if (bin.Entries.Count == 0) return;
            int n = bin.Entries.Count;
            ShowConfirm(new PendingBulk
            {
                Title = $"Permanently discard all {n} binned pins? This cannot be undone.",
                ConfirmText = $"Empty bin ({n} pins)",
                ReturnScreen = PmScreen.Bin,
                Lines = bin.Entries.Reverse().Select(e => WpCommands.SafeTitle(e.Title)).ToArray(),
                Execute = () =>
                {
                    bin.Clear();
                    binSelected.Clear();
                    notice = "Recycle bin emptied.";
                    screen = PmScreen.Bin;
                    Recompose();
                }
            });
        }

        // ------------------------------------------------------------------ share screen

        string shareKey;

        void OpenShare(PinRow row)
        {
            shareKey = row.Key;
            screen = PmScreen.Share;
            Recompose();
        }

        /// <summary>Re-resolves the shared pin (it may have been edited or deleted since the screen opened).</summary>
        Waypoint ShareTarget()
        {
            int i = svc.ResolveIndex(shareKey);
            if (i >= 0) return svc.Own[i];
            notice = "That pin no longer exists.";
            RefreshData();
            BackToMatrix();
            return null;
        }

        string ShareRelCoords(Waypoint wp)
            => $"{FmtCoord(svc.RelX(wp.Position.X))}, {FmtCoord(wp.Position.Y)}, {FmtCoord(svc.RelZ(wp.Position.Z))}";

        string ShareCmd(Waypoint wp)
            => WpCommands.ShareCommand(wp, svc.RelX(wp.Position.X), svc.RelZ(wp.Position.Z));

        string ShareChatLine(Waypoint wp)
            => WpCommands.ShareLine(wp, ShareRelCoords(wp));

        void ComposeShare(GuiComposer c)
        {
            int i = svc.ResolveIndex(shareKey);
            if (i < 0)
            {
                // Composing mid-recompose: show a stub; the poll tick will bounce back to the matrix.
                c.AddStaticText("That pin no longer exists.", CairoFont.WhiteSmallText(), EB(4, 42, DW, 25));
                c.AddSmallButton("Back", () => { BackToMatrix(); return true; }, EB(4, 76, 80, 30));
                return;
            }
            var wp = svc.Own[i];
            var font = CairoFont.WhiteSmallText();
            double y = 42;

            c.AddStaticText($"Share '{WpCommands.ShareSafeTitle(wp.Title)}' ({ShareRelCoords(wp)})", CairoFont.WhiteSmallishText(), EB(4, y, DW, 28));
            y += 36;

            c.AddStaticText("Chat message (players who also run Pin Matrix get a clickable add-link; everyone else sees the name, coordinates, icon and color):", font, EB(4, y, DW, 44));
            y += 48;
            c.AddInset(EB(2, y - 2, DW + 4, 48), 3);
            c.AddStaticText(ShareChatLine(wp), font, EB(8, y + 2, DW - 12, 44));
            y += 56;

            c.AddStaticText("Add command for Discord etc. (recipients paste it into their chat box — running it re-creates this exact pin):", font, EB(4, y, DW, 25));
            y += 28;
            c.AddInset(EB(2, y - 2, DW + 4, 48), 3);
            c.AddStaticText(ShareCmd(wp), font, EB(8, y + 2, DW - 12, 44));
            y += 60;

            c.AddSmallButton("Send to chat", () => { OnShareSend(); return true; }, EB(4, y, 130, 30));
            c.AddSmallButton("Copy command", () => { OnShareCopy(); return true; }, EB(140, y, 140, 30));
            c.AddSmallButton("Back", () => { BackToMatrix(); return true; }, EB(286, y, 80, 30));
            c.AddDynamicText("", font, EB(376, y + 6, DW - 376, 24), "shareresult");
        }

        void ShareFeedback(string text)
            => SingleComposer.GetDynamicText("shareresult")?.SetNewText(text, false, true, false);

        void OnShareSend()
        {
            var wp = ShareTarget();
            if (wp == null) return;
            // Straight to the current chat channel — deliberately not via BatchEngine (its Busy
            // flag suppresses list polling, and this is not a mutation).
            capi.SendChatMessage(ShareChatLine(wp));
            ShareFeedback("Sent to chat.");
        }

        void OnShareCopy()
        {
            var wp = ShareTarget();
            if (wp == null) return;
            capi.Input.ClipboardText = ShareCmd(wp);
            ShareFeedback("Copied — recipients paste it into their chat box.");
        }

        // ------------------------------------------------------------------ export / import screen

        string importPath = "";
        string portResult = "";

        void ComposeImportExport(GuiComposer c)
        {
            var font = CairoFont.WhiteSmallText();
            string folder = Porter.Folder(capi);
            double y = 42;

            c.AddStaticText("Files folder:", font, EB(4, y, 100, 25));
            c.AddStaticText(folder, font, EB(108, y, DW - 108, 44));
            y += 48;

            c.AddStaticText("Export (JSON: X/Z spawn-relative, Y absolute, colors as #rrggbb):", font, EB(4, y, DW, 25));
            y += 28;
            c.AddSmallButton($"Export all ({allRows.Count})", () => { DoExport(false); return true; }, EB(4, y, 160, 28));
            c.AddSmallButton($"Export selected ({selectedKeys.Count})", () => { DoExport(true); return true; }, EB(170, y, 190, 28));
            y += 40;

            c.AddStaticText("Import (additive — never wipes existing pins; duplicates by name+position are skipped):", font, EB(4, y, DW, 25));
            y += 28;
            c.AddStaticText("File path", font, EB(4, y + 4, 70, 25));
            c.AddTextInput(EB(80, y, 560, 28), t => importPath = t, font, "importpath");
            c.AddSmallButton("Load file", () => { DoImport(); return true; }, EB(650, y, 100, 28));
            y += 40;

            c.AddDynamicText(portResult, font, EB(4, y, DW, 66), "portresult");
            y += 70;
            c.AddSmallButton("Back", () => { BackToMatrix(); return true; }, EB(4, y, 80, 30));
        }

        void RestoreImportExportState()
        {
            if (importPath.Length > 0) SingleComposer.GetTextInput("importpath").SetValue(importPath);
            else SingleComposer.GetTextInput("importpath").SetPlaceHolderText("paste a full path, or a filename inside the folder above");
        }

        void DoExport(bool selectionOnly)
        {
            var wps = selectionOnly ? SelectedAllRows().Select(r => r.Wp).ToList() : allRows.Select(r => r.Wp).ToList();
            if (wps.Count == 0)
            {
                portResult = selectionOnly ? "Nothing selected to export." : "No waypoints to export.";
            }
            else
            {
                try
                {
                    string file = Porter.Export(capi, svc, wps, selectionOnly ? "pins-selection" : "pins-all");
                    portResult = $"Exported {wps.Count} pins to:\n{file}";
                    capi.ShowChatMessage("[Pin Matrix] Exported to " + file);
                }
                catch (Exception e)
                {
                    portResult = "Export failed: " + e.Message;
                }
            }
            SingleComposer.GetDynamicText("portresult")?.SetNewText(portResult, false, true, false);
        }

        void DoImport()
        {
            string path = (importPath ?? "").Trim().Trim('"');
            if (path.Length == 0)
            {
                portResult = "Enter a file path first.";
                SingleComposer.GetDynamicText("portresult")?.SetNewText(portResult, false, true, false);
                return;
            }
            if (!File.Exists(path))
            {
                string inFolder = System.IO.Path.Combine(Porter.Folder(capi), path);
                if (File.Exists(inFolder)) path = inFolder;
                else
                {
                    portResult = "File not found: " + path;
                    SingleComposer.GetDynamicText("portresult")?.SetNewText(portResult, false, true, false);
                    return;
                }
            }

            var result = Porter.Import(path, svc);

            // dedupe: skip entries whose rounded position AND name match an existing pin (spec §6)
            var existing = new HashSet<string>(svc.Own.Select(wp =>
                FormattableString.Invariant($"{(int)Math.Round(wp.Position.X)}|{(int)Math.Round(wp.Position.Y)}|{(int)Math.Round(wp.Position.Z)}|{WpCommands.SafeTitle(wp.Title)}")));
            var fresh = new List<ImportPin>();
            int dupes = 0;
            foreach (var pin in result.Valid)
            {
                string sig = FormattableString.Invariant($"{(int)Math.Round(pin.AbsX)}|{(int)Math.Round(pin.AbsY)}|{(int)Math.Round(pin.AbsZ)}|{WpCommands.SafeTitle(pin.Name)}");
                if (existing.Contains(sig)) { dupes++; continue; }
                existing.Add(sig);
                fresh.Add(pin);
            }

            string errorSummary = result.Errors.Count == 0 ? ""
                : $"\n{result.Errors.Count} invalid entries skipped: " + string.Join("; ", result.Errors.Take(4)) + (result.Errors.Count > 4 ? "; ..." : "");

            if (fresh.Count == 0)
            {
                portResult = $"Nothing to import ({dupes} duplicates skipped).{errorSummary}";
                SingleComposer.GetDynamicText("portresult")?.SetNewText(portResult, false, true, false);
                return;
            }

            ShowConfirm(new PendingBulk
            {
                Title = $"Import {fresh.Count} pins? ({dupes} duplicates and {result.Errors.Count} invalid entries skipped.)",
                ConfirmText = $"Import {fresh.Count} pins",
                ReturnScreen = PmScreen.ImportExport,
                Lines = fresh.Select(p => $"{WpCommands.SafeTitle(p.Name)} [{p.Icon}] ({FmtCoord(svc.RelX(p.AbsX))}, {FmtCoord(p.AbsY)}, {FmtCoord(svc.RelZ(p.AbsZ))})").ToArray(),
                Execute = () =>
                {
                    var cmds = fresh.Select(p => WpCommands.Add(p.Icon, p.AbsX, p.AbsY, p.AbsZ, p.Pinned, p.Color, p.Name)).ToList();
                    undo = null;
                    RunBatch(cmds, $"Imported {fresh.Count} pins ({dupes} duplicates, {result.Errors.Count} invalid skipped).");
                }
            });
        }
    }
}
