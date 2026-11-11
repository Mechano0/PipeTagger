using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Color = System.Drawing.Color;
using Control = System.Windows.Forms.Control;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace PipeTagger
{
    public enum TagScope { ActiveView, ActiveSheet, SelectedSheets, SelectedElements }

    /// <summary>Everything the user picks in the dialog.</summary>
    public class TagOptions
    {
        public MepKind Kind = MepKind.Pipes;
        /// <summary>Ticked system type -> tag type to use for it.</summary>
        public Dictionary<ElementId, ElementId> SystemToTag = new Dictionary<ElementId, ElementId>();
        public TagScope Scope = TagScope.ActiveSheet;
        public bool SkipTagged = true;
        public bool AddLeader = false;
        public bool SkipRisers = false;
        public bool CenterOnElement = true; // move tags so the text centre sits exactly on the run
        public ArrowItem Arrow;             // leader arrowhead to set on the tag types (null = keep)
        public double OffsetMm = 3;       // distance from centreline to tag, mm ON PAPER
        public double MinLengthMm = 500;  // shorter segments are skipped
        public double MaxLengthMm = 0;    // longer segments are skipped (0 = no limit)
    }

    /// <summary>Remembers the user's choices for the rest of the Revit session.</summary>
    internal static class Memory
    {
        public static MepKind Kind = MepKind.Pipes;
        public static TagScope Scope = TagScope.ActiveSheet;
        public static bool SkipTagged = true, AddLeader = false, SkipRisers = false, Center = true;
        public static Size WindowSize = Size.Empty;
        public static string ArrowName;     // last arrowhead picked        // Per kind: system name -> (ticked, tag name)
        public static readonly Dictionary<MepKind, Dictionary<string, (bool On, string Tag)>> Rows =
            new Dictionary<MepKind, Dictionary<string, (bool, string)>>
            {
                [MepKind.Pipes] = new Dictionary<string, (bool, string)>(),
                [MepKind.Ducts] = new Dictionary<string, (bool, string)>()
            };
        public static readonly Dictionary<MepKind, decimal> Offset = new Dictionary<MepKind, decimal>
            { [MepKind.Pipes] = 3, [MepKind.Ducts] = 0 };
        public static readonly Dictionary<MepKind, decimal> MinLen = new Dictionary<MepKind, decimal>
            { [MepKind.Pipes] = 500, [MepKind.Ducts] = 500 };
        public static readonly Dictionary<MepKind, decimal> MaxLen = new Dictionary<MepKind, decimal>
            { [MepKind.Pipes] = 0, [MepKind.Ducts] = 0 };
    }

    /// <summary>Options dialog built in code (no designer file). Resizable: the table grows with the window.</summary>
    internal class TagOptionsForm : Form
    {
        public TagOptions Options { get; private set; }

        private readonly Dictionary<MepKind, MepData> _data;
        private readonly Dictionary<MepKind, HashSet<ElementId>> _selSystems;
        private readonly int _selPipes, _selDucts;
        private readonly bool _activeIsTaggable, _activeIsSheet;
        private MepKind _kind;
        private bool _loading;

        // Controls
        private readonly RadioButton _rbPipes = new RadioButton { Text = "Pipes (مواسير)", AutoSize = true };
        private readonly RadioButton _rbDucts = new RadioButton { Text = "Ducts (صاج)", AutoSize = true };
        private readonly Label _gridLabel = new Label { AutoSize = false };
        private readonly DataGridView _grid = new DataGridView();
        private readonly ComboBox _bulkTag = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _warn = new Label { ForeColor = Color.Firebrick, AutoSize = false };
        private readonly RadioButton _rbView = new RadioButton { AutoSize = true };
        private readonly RadioButton _rbSheet = new RadioButton { AutoSize = true };
        private readonly RadioButton _rbSelSheets = new RadioButton { AutoSize = true };
        private readonly RadioButton _rbSelElems = new RadioButton { AutoSize = true };
        private readonly CheckBox _skipTagged = new CheckBox { AutoSize = true };
        private readonly CheckBox _leader = new CheckBox { AutoSize = true };
        private readonly CheckBox _skipRisers = new CheckBox { AutoSize = true };
        private readonly CheckBox _center = new CheckBox { AutoSize = true };
        private readonly ComboBox _arrow = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Label _arrowLabel = new Label { Text = "Arrowhead:", AutoSize = true };        private Label _offsetLabel;
        private readonly NumericUpDown _offset = new NumericUpDown();
        private readonly NumericUpDown _minLen = new NumericUpDown();
        private readonly NumericUpDown _maxLen = new NumericUpDown();
        private readonly Button _ok = new Button { Text = "Tag", DialogResult = DialogResult.OK };

        private const string ColOn = "on", ColSys = "sys", ColCount = "count", ColTag = "tag";

        // Anchor shortcuts
        private const AnchorStyles TL = AnchorStyles.Top | AnchorStyles.Left;
        private const AnchorStyles TLR = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        private const AnchorStyles BL = AnchorStyles.Bottom | AnchorStyles.Left;
        private const AnchorStyles BLR = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        private const AnchorStyles BR = AnchorStyles.Bottom | AnchorStyles.Right;
        private const AnchorStyles All = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        public TagOptionsForm(Dictionary<MepKind, MepData> data, string activeViewName, bool activeIsTaggable,
                              bool activeIsSheet, int selectedSheetCount, int selPipes, int selDucts,
                              Dictionary<MepKind, HashSet<ElementId>> selectedSystems,
                              List<ArrowItem> arrows, int defaultArrow)
        {
            _data = data;
            _selPipes = selPipes;
            _selDucts = selDucts;
            _selSystems = selectedSystems;
            _activeIsTaggable = activeIsTaggable;
            _activeIsSheet = activeIsSheet;

            Text = "MEP Tagger - Pipes & Ducts";
            FormBorderStyle = FormBorderStyle.Sizable;       // resizable window
            MaximizeBox = true;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            // Lay everything out for this base size; anchors take care of resizing.
            const int W = 760, H = 790;
            ClientSize = new Size(W, H);

            // ---- 1. Pipes or ducts ----
            Add(new Label { Text = "Tag:", Location = new Point(12, 14), AutoSize = true }, TL);
            _rbPipes.Location = new Point(60, 12);
            _rbDucts.Location = new Point(200, 12);
            Add(_rbPipes, TL);
            Add(_rbDucts, TL);
            _rbPipes.CheckedChanged += (s, e) => { if (_rbPipes.Checked) SwitchKind(MepKind.Pipes); };
            _rbDucts.CheckedChanged += (s, e) => { if (_rbDucts.Checked) SwitchKind(MepKind.Ducts); };

            // ---- 2. Systems table (grows with the window) ----
            _gridLabel.Location = new Point(12, 42);
            _gridLabel.Size = new Size(W - 24, 20);
            Add(_gridLabel, TLR);

            _grid.Location = new Point(12, 64);
            _grid.Size = new Size(W - 24, 268);
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.AllowUserToResizeColumns = true;
            _grid.RowHeadersVisible = false;
            _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _grid.EditMode = DataGridViewEditMode.EditOnEnter;
            _grid.BackgroundColor = Color.White;
            _grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            _grid.DataError += (s, e) => { e.ThrowException = false; };
            Add(_grid, All);

            // ---- 3. Bulk buttons ----
            int by = 340;
            var all = new Button { Text = "All", Location = new Point(12, by), Size = new Size(50, 26) };
            var none = new Button { Text = "None", Location = new Point(66, by), Size = new Size(54, 26) };
            all.Click += (s, e) => SetAllChecked(true);
            none.Click += (s, e) => SetAllChecked(false);
            Add(all, BL);
            Add(none, BL);
            Add(new Label { Text = "Tag for ticked:", Location = new Point(130, by + 5), AutoSize = true }, BL);
            _bulkTag.Location = new Point(222, by + 1);
            _bulkTag.Width = W - 222 - 12 - 96;
            Add(_bulkTag, BLR);
            var apply = new Button { Text = "Apply", Location = new Point(W - 12 - 90, by), Size = new Size(90, 26) };
            apply.Click += (s, e) => ApplyBulkTag();
            Add(apply, BR);

            _warn.Location = new Point(12, 372);
            _warn.Size = new Size(W - 24, 20);
            Add(_warn, BLR);

            // ---- 4. Scope ----
            var grp = new GroupBox { Text = "Where", Location = new Point(12, 396), Size = new Size(W - 24, 128) };
            Add(grp, BLR);
            _rbView.Text = $"Active view  ({activeViewName})";
            _rbView.Enabled = activeIsTaggable;
            _rbSheet.Text = activeIsSheet ? $"All views on active sheet  ({activeViewName})"
                                          : "All views on active sheet  (open a sheet first)";
            _rbSheet.Enabled = activeIsSheet;
            _rbSelSheets.Text = $"Sheets selected in Project Browser  ({selectedSheetCount})";
            _rbSelSheets.Enabled = selectedSheetCount > 0;
            int gy = 22;
            foreach (RadioButton rb in new[] { _rbView, _rbSheet, _rbSelSheets, _rbSelElems })
            {
                rb.Location = new Point(12, gy);
                grp.Controls.Add(rb);
                gy += 25;
            }

            // ---- 5. Options ----
            int y = 534;
            _skipTagged.Text = "Skip elements that already have the same tag";
            _leader.Text = "Add leader";
            _skipRisers.Text = "Skip risers (seen end-on, e.g. vertical pipes/ducts in plan)";
            _skipTagged.Checked = Memory.SkipTagged;
            _leader.Checked = Memory.AddLeader;
            _skipRisers.Checked = Memory.SkipRisers;
            _center.Text = "Centre the tag exactly on the duct / pipe (length and width)";
            _center.Checked = Memory.Center;
            _leader.Text = "Add leader (straight from the middle, text parallel to the run)";

            // Arrowhead list: default = a 30° arrow (or one that will be created)
            _arrow.Items.AddRange(arrows.Cast<object>().ToArray());
            int lastArrow = arrows.FindIndex(a => a.Name == Memory.ArrowName);
            _arrow.SelectedIndex = lastArrow >= 0 ? lastArrow : Math.Max(0, defaultArrow);
            _arrow.DropDownWidth = DropDownWidthFor(arrows.Select(a => a.Name));

            foreach (CheckBox cb in new[] { _center, _skipTagged, _leader, _skipRisers })
            {
                cb.Location = new Point(14, y);
                Add(cb, BL);
                if (cb == _leader)
                {
                    // Arrowhead picker sits on the same row as "Add leader"
                    _arrowLabel.Location = new Point(400, y + 2);
                    _arrow.Location = new Point(474, y - 1);
                    _arrow.Width = W - 474 - 12;
                    Add(_arrowLabel, BL);
                    Add(_arrow, BLR);
                }
                y += 25;
            }
            y += 6;

            _offsetLabel = AddNumberRow("Offset from centreline (mm on paper):", _offset, 0, 50, 1, 1, ref y);

            // Leader on: no centring, arrowhead + "clearance" meaning for the distance field
            _leader.CheckedChanged += (s, e) => UpdateLeaderUi();
            AddNumberRow("Minimum length (mm):", _minLen, 0, 100000, 0, 100, ref y);
            AddNumberRow("Maximum length (mm, 0 = no limit):", _maxLen, 0, 1000000, 0, 500, ref y);

            // ---- 6. Buttons ----
            _ok.Location = new Point(W - 12 - 80 - 6 - 80, H - 40);
            _ok.Size = new Size(80, 28);
            _ok.Click += OnOk;
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(W - 12 - 80, H - 40), Size = new Size(80, 28) };
            Add(_ok, BR);
            Add(cancel, BR);
            AcceptButton = _ok;
            CancelButton = cancel;

            // Can't shrink below the point where the lower controls overlap the table
            MinimumSize = new Size(Width - 160, Height - 180);
            if (!Memory.WindowSize.IsEmpty)
                Size = new Size(Math.Max(Memory.WindowSize.Width, MinimumSize.Width),
                                Math.Max(Memory.WindowSize.Height, MinimumSize.Height));
            FormClosing += (s, e) => Memory.WindowSize = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;

            // ---- Initial state ----
            // Open on the kind the user selected in Revit, else the last one used
            bool pipesSel = _selSystems[MepKind.Pipes].Count > 0;
            bool ductsSel = _selSystems[MepKind.Ducts].Count > 0;
            MepKind start = ductsSel && !pipesSel ? MepKind.Ducts
                          : pipesSel && !ductsSel ? MepKind.Pipes
                          : Memory.Kind;
            if (start == MepKind.Ducts) _rbDucts.Checked = true; else _rbPipes.Checked = true;

            // Something selected -> "selected only"; otherwise the last scope used
            RadioButton preferred = _rbSelElems.Enabled ? _rbSelElems : ScopeButton(Memory.Scope);
            if (preferred.Enabled) preferred.Checked = true;
            else if (_rbSheet.Enabled) _rbSheet.Checked = true;
            else if (_rbSelSheets.Enabled) _rbSelSheets.Checked = true;
            else if (_rbView.Enabled) _rbView.Checked = true;

            UpdateLeaderUi();
        }

        private void UpdateLeaderUi()
        {
            bool leader = _leader.Checked;
            _center.Enabled = !leader;
            _arrow.Enabled = _arrowLabel.Enabled = leader;            _offsetLabel.Text = leader ? "Clearance from duct / pipe edge (mm on paper):"
                                       : "Offset from centreline (mm on paper):";
            if (leader && _offset.Value < 2) _offset.Value = 4;   // a tag right on the edge looks cramped
        }

        // ---------------------------------------------------------------------
        //  Switch between Pipes and Ducts: rebuild the table
        // ---------------------------------------------------------------------
        private void SwitchKind(MepKind kind)
        {
            if (!_loading) SaveToMemory();   // keep what the user did on the other kind
            _kind = kind;
            _loading = true;

            MepData d = _data[kind];
            HashSet<ElementId> fromSel = _selSystems[kind];
            List<string> tagNames = d.TagTypes.Select(t => t.Name).ToList();
            string defaultTag = d.TagTypes.FirstOrDefault(t => !t.IsMultiCategory)?.Name ?? tagNames.FirstOrDefault();

            _grid.Columns.Clear();
            _grid.Rows.Clear();
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = ColOn, HeaderText = "✓", Width = 34 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = ColSys, HeaderText = "System Type", ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 35, MinimumWidth = 140
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = ColCount, HeaderText = "In model", Width = 70, ReadOnly = true });
            var tagCol = new DataGridViewComboBoxColumn
            {
                Name = ColTag,
                HeaderText = "Tag Type",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 65,
                MinimumWidth = 200,
                FlatStyle = FlatStyle.Flat,
                // Dropdown list as wide as the longest tag name, so names are never cut
                DropDownWidth = DropDownWidthFor(tagNames)
            };
            tagCol.Items.AddRange(tagNames.Cast<object>().ToArray());
            _grid.Columns.Add(tagCol);

            // Systems of the selected elements go to the top, ticked and highlighted
            var mem = Memory.Rows[kind];
            var bold = new Font(_grid.Font, FontStyle.Bold);
            IEnumerable<SystemRow> ordered = d.Systems.OrderByDescending(s => fromSel.Contains(s.Id));
            foreach (SystemRow s in ordered)
            {
                bool inSel = fromSel.Contains(s.Id);
                bool on = fromSel.Count > 0 ? inSel : false;
                string tag = defaultTag;
                if (mem.TryGetValue(s.Name, out var m))
                {
                    if (fromSel.Count == 0) on = m.On;
                    if (m.Tag != null && tagNames.Contains(m.Tag)) tag = m.Tag;
                }
                int r = _grid.Rows.Add(on, s.Name, s.Count, tag);
                DataGridViewRow row = _grid.Rows[r];
                row.Tag = s;
                if (inSel)
                {
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 205);
                    row.DefaultCellStyle.Font = bold;
                }
                else if (s.Count == 0)
                {
                    row.DefaultCellStyle.ForeColor = Color.Gray;
                }
            }

            // Header line above the table
            if (fromSel.Count > 0)
            {
                string names = string.Join(", ", d.Systems.Where(s => fromSel.Contains(s.Id)).Select(s => s.Name));
                _gridLabel.Text = $"From your selection: {names}  →  choose the tag:";
                _gridLabel.ForeColor = Color.DarkGreen;
                _gridLabel.Font = new Font(Font, FontStyle.Bold);
            }
            else
            {
                _gridLabel.Text = "Tick the System Types to tag and choose the tag for each one:";
                _gridLabel.ForeColor = SystemColorsText;
                _gridLabel.Font = Font;
            }

            _bulkTag.Items.Clear();
            _bulkTag.Items.AddRange(tagNames.Cast<object>().ToArray());
            _bulkTag.DropDownWidth = Math.Max(_bulkTag.Width, DropDownWidthFor(tagNames));
            if (_bulkTag.Items.Count > 0) _bulkTag.SelectedItem = defaultTag;

            _warn.Text = tagNames.Count == 0
                ? (kind == MepKind.Pipes
                    ? "No Pipe Tag family loaded. Insert > Load Family > Annotations > Mechanical > Pipe Size Tag"
                    : "No Duct Tag family loaded. Insert > Load Family > Annotations > Mechanical > Duct Size Tag")
                : "";

            int sel = kind == MepKind.Pipes ? _selPipes : _selDucts;
            string where = _activeIsTaggable ? "in active view" : "in the views of this sheet";
            _rbSelElems.Text = $"Selected {(kind == MepKind.Pipes ? "pipes" : "ducts")} only, {where}  ({sel})";
            _rbSelElems.Enabled = sel > 0 && (_activeIsTaggable || _activeIsSheet);
            if (_rbSelElems.Checked && !_rbSelElems.Enabled)
            {
                if (_rbSheet.Enabled) _rbSheet.Checked = true;
                else if (_rbView.Enabled) _rbView.Checked = true;
            }

            _offset.Value = Clamp(_offset, Memory.Offset[kind]);
            _minLen.Value = Clamp(_minLen, Memory.MinLen[kind]);
            _maxLen.Value = Clamp(_maxLen, Memory.MaxLen[kind]);

            _ok.Enabled = tagNames.Count > 0;
            _loading = false;
        }

        private static readonly Color SystemColorsText = System.Drawing.SystemColors.ControlText;

        /// <summary>Pixel width that fits the longest tag name (capped to the screen).</summary>
        private int DropDownWidthFor(IEnumerable<string> names)
        {
            int widest = 200;
            foreach (string n in names)
                widest = Math.Max(widest, TextRenderer.MeasureText(n, Font).Width);
            int screen = Screen.FromControl(this).WorkingArea.Width - 40;
            return Math.Min(widest + SystemInformation.VerticalScrollBarWidth + 16, screen);
        }

        private void SetAllChecked(bool on)
        {
            foreach (DataGridViewRow r in _grid.Rows) r.Cells[ColOn].Value = on;
        }

        private void ApplyBulkTag()
        {
            if (_bulkTag.SelectedItem == null) return;
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Cells[ColOn].Value is bool b && b)
                    r.Cells[ColTag].Value = _bulkTag.SelectedItem;
        }

        private void SaveToMemory()
        {
            if (_grid.Columns.Count == 0) return;   // first load: nothing to save yet
            var mem = Memory.Rows[_kind];
            foreach (DataGridViewRow r in _grid.Rows)
            {
                var s = (SystemRow)r.Tag;
                mem[s.Name] = (r.Cells[ColOn].Value is bool b && b, r.Cells[ColTag].Value as string);
            }
            Memory.Offset[_kind] = _offset.Value;
            Memory.MinLen[_kind] = _minLen.Value;
            Memory.MaxLen[_kind] = _maxLen.Value;
        }

        // ---------------------------------------------------------------------
        //  OK: validate and build the TagOptions
        // ---------------------------------------------------------------------
        private void OnOk(object sender, EventArgs e)
        {
            _grid.EndEdit();
            MepData d = _data[_kind];
            var map = new Dictionary<ElementId, ElementId>();

            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (!(r.Cells[ColOn].Value is bool b && b)) continue;
                string tagName = r.Cells[ColTag].Value as string;
                TagTypeItem tag = d.TagTypes.FirstOrDefault(t => t.Name == tagName);
                if (tag == null) continue;
                map[((SystemRow)r.Tag).Id] = tag.Id;
            }

            if (map.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one System Type and choose its tag.", "MEP Tagger",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;   // keep the dialog open
                return;
            }

            if (_maxLen.Value > 0 && _maxLen.Value < _minLen.Value)
            {
                MessageBox.Show(this, "Maximum length is smaller than minimum length.", "MEP Tagger",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            Options = new TagOptions
            {
                Kind = _kind,
                SystemToTag = map,
                Scope = _rbSelElems.Checked ? TagScope.SelectedElements
                      : _rbSheet.Checked ? TagScope.ActiveSheet
                      : _rbSelSheets.Checked ? TagScope.SelectedSheets
                      : TagScope.ActiveView,
                SkipTagged = _skipTagged.Checked,
                AddLeader = _leader.Checked,
                SkipRisers = _skipRisers.Checked,
                CenterOnElement = _center.Checked,
                Arrow = _leader.Checked ? _arrow.SelectedItem as ArrowItem : null,                OffsetMm = (double)_offset.Value,
                MinLengthMm = (double)_minLen.Value,
                MaxLengthMm = (double)_maxLen.Value
            };

            // Remember for next time ("selected only" is not remembered: it depends on the selection)
            SaveToMemory();
            Memory.Kind = _kind;
            if (Options.Scope != TagScope.SelectedElements) Memory.Scope = Options.Scope;
            Memory.SkipTagged = Options.SkipTagged;
            Memory.AddLeader = Options.AddLeader;
            Memory.SkipRisers = Options.SkipRisers;
            Memory.Center = Options.CenterOnElement;
            if (_arrow.SelectedItem is ArrowItem a) Memory.ArrowName = a.Name;        }

        // ---------------------------------------------------------------------
        //  Small helpers
        // ---------------------------------------------------------------------
        private RadioButton ScopeButton(TagScope s) =>
            s == TagScope.ActiveSheet ? _rbSheet :
            s == TagScope.SelectedSheets ? _rbSelSheets :
            s == TagScope.SelectedElements ? _rbSelElems : _rbView;

        private Label AddNumberRow(string label, NumericUpDown n, decimal min, decimal max,
                                   int decimals, decimal step, ref int y)
        {
            var lbl = new Label { Text = label, Location = new Point(14, y + 3), AutoSize = true };
            Add(lbl, BL);
            n.Minimum = min;
            n.Maximum = max;
            n.DecimalPlaces = decimals;
            n.Increment = step;
            n.Location = new Point(320, y);
            n.Width = 110;
            Add(n, BL);
            y += 30;
            return lbl;
        }

        private static decimal Clamp(NumericUpDown n, decimal v) => Math.Min(n.Maximum, Math.Max(n.Minimum, v));

        private void Add(Control c, AnchorStyles anchor)
        {
            c.Anchor = anchor;
            Controls.Add(c);
        }
    }
}
