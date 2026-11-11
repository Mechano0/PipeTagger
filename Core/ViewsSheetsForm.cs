using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using FontStyle = System.Drawing.FontStyle;
using Form = System.Windows.Forms.Form;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace PipeTagger
{
    /// <summary>Remembers the last choices for the rest of the Revit session.</summary>
    internal static class ViewsSheetsMemory
    {
        public static HashSet<string> Ticked = new HashSet<string>();
        public static int Mode = 0;                 // 0 Independent, 1 With detailing, 2 Dependent
        public static string Pattern = "{View} - {ScopeBox}";
        public static string Template, TitleBlock, CopyFrom, VpType;
        public static decimal Scale = 50;
        public static bool PlaceOnSheets = true;
        public static string FirstNo = "", LastNo = "", FirstName = "", LastName = "";
    }

    /// <summary>Options dialog for "Views from Scope Boxes", built in code (no designer file).</summary>
    internal class ViewsSheetsForm : Form
    {
        public ViewsSheetsOptions Options { get; private set; }

        private readonly ViewsSheetsData _data;
        private const string Keep = "<Keep as main view>", NoneTxt = "<None>", SameAsCopy = "<Same as copied sheet>";

        // Scope boxes
        private readonly TextBox _filter = new TextBox();
        private readonly CheckedListBox _boxes = new CheckedListBox { CheckOnClick = true, IntegralHeight = false };
        private readonly Label _count = new Label { AutoSize = true };
        private readonly HashSet<ElementId> _ticked = new HashSet<ElementId>();
        private bool _refilling;

        // Views
        private readonly RadioButton _rbInd = new RadioButton { Text = "Independent", AutoSize = true };
        private readonly RadioButton _rbDet = new RadioButton { Text = "Independent with detailing", AutoSize = true };
        private readonly RadioButton _rbDep = new RadioButton { Text = "Dependent", AutoSize = true };
        private readonly TextBox _pattern = new TextBox();
        private readonly Label _namePreview = new Label { AutoSize = false, ForeColor = Color.DimGray };
        private readonly ComboBox _template = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown _scale = new NumericUpDown { Minimum = 0, Maximum = 10000 };

        // Sheets
        private readonly CheckBox _place = new CheckBox { Text = "Put each view on a sheet", AutoSize = true };
        private readonly ComboBox _titleBlock = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _copyFrom = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _vpType = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _firstNo = new TextBox(), _lastNo = new TextBox();
        private readonly TextBox _firstName = new TextBox(), _lastName = new TextBox();
        private readonly Label _sheetPreview = new Label { AutoSize = false, ForeColor = Color.DimGray };
        private GroupBox _sheetGroup;

        private readonly Label _warn = new Label { AutoSize = false, ForeColor = Color.Firebrick };
        private readonly Button _ok = new Button { Text = "Create", Size = new Size(100, 30) };

        private const AnchorStyles TL = AnchorStyles.Top | AnchorStyles.Left;
        private const AnchorStyles TLR = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        private const AnchorStyles TBL = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        private const AnchorStyles BR = AnchorStyles.Bottom | AnchorStyles.Right;
        private const AnchorStyles BLR = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        public ViewsSheetsForm(ViewsSheetsData data)
        {
            _data = data;
            Text = "Views from Scope Boxes  →  Sheets";
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(860, 640);
            MinimumSize = new Size(860, 640);

            // ---------- Header ----------
            Controls.Add(new Label
            {
                Text = "Main view:  " + data.Source.Name + $"   (1:{data.Source.Scale})",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = false,
                Location = new Point(12, 10), Size = new Size(836, 22), Anchor = TLR
            });

            // ---------- Left: scope boxes ----------
            Controls.Add(new Label { Text = "Scope boxes (one view each):", Location = new Point(12, 40), AutoSize = true });
            Controls.Add(new Label { Text = "Filter:", Location = new Point(12, 66), AutoSize = true });
            _filter.SetBounds(55, 62, 225, 23);
            _filter.Anchor = TL;
            Controls.Add(_filter);
            _boxes.SetBounds(12, 92, 268, 470);
            _boxes.Anchor = TBL;
            Controls.Add(_boxes);
            var all = new Button { Text = "All shown", Location = new Point(12, 568), Size = new Size(85, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var none = new Button { Text = "None", Location = new Point(102, 568), Size = new Size(60, 26), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            _count.Location = new Point(170, 573);
            _count.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.AddRange(new System.Windows.Forms.Control[] { all, none, _count });

            // ---------- Right: views ----------
            var vg = new GroupBox { Text = "New views", Location = new Point(295, 40), Size = new Size(553, 190), Anchor = TLR };
            Controls.Add(vg);
            _rbInd.Location = new Point(12, 24);
            _rbDet.Location = new Point(122, 24);
            _rbDep.Location = new Point(305, 24);
            vg.Controls.AddRange(new System.Windows.Forms.Control[] { _rbInd, _rbDet, _rbDep });

            vg.Controls.Add(new Label { Text = "View name:", Location = new Point(12, 58), AutoSize = true });
            _pattern.SetBounds(100, 54, 440, 23);
            _pattern.Anchor = TLR;
            vg.Controls.Add(_pattern);
            vg.Controls.Add(new Label
            {
                Text = "{View} = main view name,  {ScopeBox} = scope box name,  {N} = last number in the scope box name",
                Location = new Point(100, 80), AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8f)
            });
            _namePreview.SetBounds(100, 98, 440, 18);
            _namePreview.Anchor = TLR;
            vg.Controls.Add(_namePreview);

            vg.Controls.Add(new Label { Text = "View template:", Location = new Point(12, 126), AutoSize = true });
            _template.SetBounds(100, 122, 440, 23);
            _template.Anchor = TLR;
            vg.Controls.Add(_template);

            vg.Controls.Add(new Label { Text = "Scale  1 :", Location = new Point(12, 158), AutoSize = true });
            _scale.SetBounds(100, 155, 80, 23);
            vg.Controls.Add(_scale);
            vg.Controls.Add(new Label { Text = "(0 = keep)", Location = new Point(186, 158), AutoSize = true, ForeColor = Color.DimGray });

            // ---------- Right: sheets ----------
            _place.Location = new Point(295, 240);
            Controls.Add(_place);
            _sheetGroup = new GroupBox { Text = "Sheets", Location = new Point(295, 262), Size = new Size(553, 300), Anchor = TLR };
            Controls.Add(_sheetGroup);

            int y = 24;
            AddRow("Title block:", _titleBlock, ref y);
            AddRow("Copy from sheet:", _copyFrom, ref y);
            _sheetGroup.Controls.Add(new Label
            {
                Text = "Copies its parameters, revisions, title block values and the view's position on it.",
                Location = new Point(130, y - 6), AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8f)
            });
            y += 14;
            AddRow("Viewport type:", _vpType, ref y);

            _sheetGroup.Controls.Add(new Label { Text = "First", Location = new Point(130, y), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
            _sheetGroup.Controls.Add(new Label { Text = "Last", Location = new Point(340, y), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
            y += 20;
            AddPair("Sheet number:", _firstNo, _lastNo, ref y);
            AddPair("Sheet name:", _firstName, _lastName, ref y);
            _sheetPreview.SetBounds(12, y, 528, 36);
            _sheetPreview.Anchor = TLR;
            _sheetGroup.Controls.Add(_sheetPreview);
            _sheetGroup.Controls.Add(new Label
            {
                Text = "Only the number that changes between First and Last counts up. Sheets that already exist are reused.",
                Location = new Point(12, y + 38), AutoSize = true, ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8f)
            });

            // ---------- Bottom ----------
            _warn.SetBounds(295, 568, 440, 50);
            _warn.Anchor = BLR;
            Controls.Add(_warn);
            _ok.Location = new Point(748, 568);
            _ok.Anchor = BR;
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 30), Location = new Point(748, 602), Anchor = BR };
            Controls.AddRange(new System.Windows.Forms.Control[] { _ok, cancel });
            AcceptButton = _ok;
            CancelButton = cancel;

            // ---------- Fill ----------
            foreach (ScopeBoxItem s in data.ScopeBoxes)
                if (ViewsSheetsMemory.Ticked.Contains(s.Name)) _ticked.Add(s.Id);

            _template.Items.Add(new NamedId { Name = Keep });
            _template.Items.Add(new NamedId { Name = NoneTxt });
            foreach (NamedId t in data.Templates) _template.Items.Add(t);
            Select(_template, ViewsSheetsMemory.Template, 0);

            foreach (NamedId t in data.TitleBlocks) _titleBlock.Items.Add(t);
            Select(_titleBlock, ViewsSheetsMemory.TitleBlock, 0);

            _copyFrom.Items.Add(new NamedId { Name = NoneTxt });
            foreach (NamedId s in data.Sheets) _copyFrom.Items.Add(s);
            Select(_copyFrom, ViewsSheetsMemory.CopyFrom, 0);

            _vpType.Items.Add(new NamedId { Name = SameAsCopy });
            foreach (NamedId t in data.ViewportTypes) _vpType.Items.Add(t);
            Select(_vpType, ViewsSheetsMemory.VpType, 0);

            new[] { _rbInd, _rbDet, _rbDep }[Math.Max(0, Math.Min(2, ViewsSheetsMemory.Mode))].Checked = true;
            _pattern.Text = ViewsSheetsMemory.Pattern;
            _scale.Value = ViewsSheetsMemory.Scale;
            _place.Checked = ViewsSheetsMemory.PlaceOnSheets;
            _firstNo.Text = ViewsSheetsMemory.FirstNo;
            _lastNo.Text = ViewsSheetsMemory.LastNo;
            _firstName.Text = ViewsSheetsMemory.FirstName;
            _lastName.Text = ViewsSheetsMemory.LastName;

            RefillBoxes();

            // ---------- Events ----------
            _filter.TextChanged += (s, e) => RefillBoxes();
            _boxes.ItemCheck += (s, e) =>
            {
                if (_refilling) return;
                var item = (ScopeBoxItem)_boxes.Items[e.Index];
                if (e.NewValue == CheckState.Checked) _ticked.Add(item.Id); else _ticked.Remove(item.Id);
                BeginInvoke((Action)UpdatePreview);
            };
            all.Click += (s, e) => SetAllShown(true);
            none.Click += (s, e) => { _ticked.Clear(); RefillBoxes(); };
            foreach (var c in new System.Windows.Forms.Control[] { _pattern, _firstNo, _lastNo, _firstName, _lastName })
                c.TextChanged += (s, e) => UpdatePreview();
            _rbDep.CheckedChanged += (s, e) => UpdatePreview();
            _place.CheckedChanged += (s, e) => UpdatePreview();
            _ok.Click += (s, e) => OnOk();

            UpdatePreview();
        }

        // =====================================================================
        private void AddRow(string label, System.Windows.Forms.Control c, ref int y)
        {
            _sheetGroup.Controls.Add(new Label { Text = label, Location = new Point(12, y + 4), AutoSize = true });
            c.SetBounds(130, y, 410, 23);
            c.Anchor = TLR;
            _sheetGroup.Controls.Add(c);
            y += 32;
        }

        private void AddPair(string label, TextBox a, TextBox b, ref int y)
        {
            _sheetGroup.Controls.Add(new Label { Text = label, Location = new Point(12, y + 4), AutoSize = true });
            a.SetBounds(130, y, 200, 23);
            b.SetBounds(340, y, 200, 23);
            b.Anchor = TLR;
            _sheetGroup.Controls.Add(a);
            _sheetGroup.Controls.Add(b);
            y += 32;
        }

        private static void Select(ComboBox cb, string name, int fallback)
        {
            int i = name == null ? -1 : cb.Items.Cast<object>().ToList().FindIndex(o => o.ToString() == name);
            cb.SelectedIndex = i >= 0 ? i : (cb.Items.Count > fallback ? fallback : -1);
        }

        private void RefillBoxes()
        {
            _refilling = true;
            _boxes.BeginUpdate();
            _boxes.Items.Clear();
            string f = _filter.Text.Trim();
            foreach (ScopeBoxItem s in _data.ScopeBoxes)
            {
                if (f.Length > 0 && s.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _boxes.Items.Add(s, _ticked.Contains(s.Id));
            }
            _boxes.EndUpdate();
            _refilling = false;
            UpdatePreview();
        }

        private void SetAllShown(bool on)
        {
            foreach (ScopeBoxItem s in _boxes.Items)
                if (on) _ticked.Add(s.Id); else _ticked.Remove(s.Id);
            RefillBoxes();
        }

        private List<ScopeBoxItem> TickedInOrder() =>
            _data.ScopeBoxes.Where(s => _ticked.Contains(s.Id)).ToList();   // already natural-sorted

        private void UpdatePreview()
        {
            List<ScopeBoxItem> ticked = TickedInOrder();
            _count.Text = $"{ticked.Count} ticked";

            var tmp = new ViewsSheetsOptions { NamePattern = _pattern.Text };
            _namePreview.Text = ticked.Count == 0 ? "e.g. —"
                : "e.g. " + tmp.NameFor(_data.Source.Name, ticked[0].Name) +
                  (ticked.Count > 1 ? "   …   " + tmp.NameFor(_data.Source.Name, ticked[ticked.Count - 1].Name) : "");

            _template.Enabled = !_rbDep.Checked;    // dependents always follow the main view
            _sheetGroup.Enabled = _place.Checked;

            string err = Validate(ticked, out _, out _);
            _warn.Text = err ?? "";
            if (_place.Checked && err == null && ticked.Count > 0)
                _sheetPreview.Text = $"{ticked[0].Name}  →  {_firstNo.Text.Trim()}  {_firstName.Text.Trim()}\n" +
                                     (ticked.Count > 1 ? $"{ticked[ticked.Count - 1].Name}  →  {_lastNo.Text.Trim()}  {_lastName.Text.Trim()}" : "");
            else
                _sheetPreview.Text = "";
        }

        private string Validate(List<ScopeBoxItem> ticked, out List<string> numbers, out List<string> names)
        {
            numbers = names = null;
            if (ticked.Count == 0) return "Tick at least one scope box.";
            if (string.IsNullOrWhiteSpace(_pattern.Text)) return "Enter a view name.";
            if (!_pattern.Text.Contains("{ScopeBox}") && !_pattern.Text.Contains("{N}") && ticked.Count > 1)
                return "The view name needs {ScopeBox} or {N} so each view gets its own name.";
            if (!_place.Checked) return null;

            numbers = Series.Build(_firstNo.Text, _lastNo.Text, ticked.Count, out string e1);
            if (numbers == null) return "Sheet number: " + e1;
            names = Series.Build(_firstName.Text, _lastName.Text, ticked.Count, out string e2);
            if (names == null) return "Sheet name: " + e2;
            if (_titleBlock.SelectedItem == null) return "This model has no title block family loaded.";
            return null;
        }

        private void OnOk()
        {
            List<ScopeBoxItem> ticked = TickedInOrder();
            string err = Validate(ticked, out List<string> numbers, out List<string> names);
            if (err != null) { _warn.Text = err; return; }

            var tpl = (NamedId)_template.SelectedItem;
            var cf = (NamedId)_copyFrom.SelectedItem;
            var vt = (NamedId)_vpType.SelectedItem;

            Options = new ViewsSheetsOptions
            {
                ScopeBoxes = ticked,
                Mode = _rbDep.Checked ? ViewDuplicateOption.AsDependent
                     : _rbDet.Checked ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate,
                NamePattern = _pattern.Text.Trim(),
                TemplateChoice = tpl == null || tpl.Name == Keep ? null
                               : tpl.Name == NoneTxt ? ElementId.InvalidElementId : tpl.Id,
                Scale = (int)_scale.Value,
                PlaceOnSheets = _place.Checked,
                TitleBlockType = ((NamedId)_titleBlock.SelectedItem)?.Id,
                CopyFromSheet = cf?.Id,
                ViewportType = vt?.Id,
                SheetNumbers = numbers ?? new List<string>(),
                SheetNames = names ?? new List<string>()
            };

            // Remember for next time
            ViewsSheetsMemory.Ticked = new HashSet<string>(ticked.Select(t => t.Name));
            ViewsSheetsMemory.Mode = _rbDep.Checked ? 2 : _rbDet.Checked ? 1 : 0;
            ViewsSheetsMemory.Pattern = _pattern.Text;
            ViewsSheetsMemory.Template = tpl?.Name;
            ViewsSheetsMemory.TitleBlock = _titleBlock.SelectedItem?.ToString();
            ViewsSheetsMemory.CopyFrom = cf?.Name;
            ViewsSheetsMemory.VpType = vt?.Name;
            ViewsSheetsMemory.Scale = _scale.Value;
            ViewsSheetsMemory.PlaceOnSheets = _place.Checked;
            ViewsSheetsMemory.FirstNo = _firstNo.Text; ViewsSheetsMemory.LastNo = _lastNo.Text;
            ViewsSheetsMemory.FirstName = _firstName.Text; ViewsSheetsMemory.LastName = _lastName.Text;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
