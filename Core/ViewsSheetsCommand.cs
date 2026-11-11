using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PipeTagger
{
    /// <summary>
    /// "Views from Scope Boxes" button. MODIFIES THE MODEL.
    /// 1) Takes the source plan (active view, a selected viewport, or a view selected in the Project Browser).
    /// 2) For every scope box the user ticks, makes a new view of it (Independent, Independent with
    ///    detailing, or Dependent) and assigns that scope box -> the view is cropped to that part.
    /// 3) Optionally puts each new view on a sheet: existing sheets are reused (matched by number),
    ///    missing ones are created with the chosen title block, numbered/named from first -> last.
    /// Everything runs in one transaction: a single Ctrl+Z undoes the whole run.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ViewsSheetsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) { message = "Open a project first."; return Result.Failed; }
            Document doc = uidoc.Document;

            // ---------- 1. Source view ----------
            View source = FindSourceView(uidoc);
            if (source == null)
            {
                TaskDialog.Show("Views & Sheets",
                    "Open the main plan, or select its viewport on a sheet,\n" +
                    "or select the view in the Project Browser, then run the tool again.");
                return Result.Cancelled;
            }

            // ---------- 2. Model data for the dialog ----------
            var data = ViewsSheetsData.Load(doc, source);
            if (data.ScopeBoxes.Count == 0)
            {
                TaskDialog.Show("Views & Sheets", "This model has no scope boxes.");
                return Result.Cancelled;
            }

            ViewsSheetsOptions opt;
            using (var form = new ViewsSheetsForm(data))
            {
                if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
                opt = form.Options;
            }

            // ---------- 3. Do it (one transaction) ----------
            var report = new StringBuilder();
            int madeViews = 0, reusedViews = 0, madeSheets = 0, placed = 0, resetCropShapes = 0;

            using (var t = new Transaction(doc, "Views from Scope Boxes"))
            {
                t.Start();
                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(fho);

                try
                {
                    var viewsByName = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .Where(v => !v.IsTemplate)
                        .GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First());

                    // --- 3a. Views ---
                    var newViews = new List<View>();
                    foreach (ScopeBoxItem sb in opt.ScopeBoxes)
                    {
                        string name = opt.NameFor(source.Name, sb.Name);
                        View v;
                        if (viewsByName.TryGetValue(name, out v))
                        {
                            reusedViews++;
                        }
                        else
                        {
                            v = doc.GetElement(source.Duplicate(opt.Mode)) as View;
                            v.Name = name;
                            viewsByName[name] = v;
                            madeViews++;
                        }

                        // Template first (it may control scale / crop)
                        if (opt.Mode != ViewDuplicateOption.AsDependent && opt.TemplateChoice != null)
                            v.ViewTemplateId = opt.TemplateChoice;

                        // Scope box -> crops the view to that part
                        if (!v.CropBoxActive) v.CropBoxActive = true;
                        string sbWhy;
                        if (!TrySetScopeBox(doc, v, sb.Id, out sbWhy))
                            report.AppendLine($"⚠ {name}: scope box not applied ({sbWhy})");
                        else if (sbWhy != null)
                            resetCropShapes++;

                        if (opt.Scale > 0 && v.Scale != opt.Scale && opt.Mode != ViewDuplicateOption.AsDependent)
                        {
                            Parameter sp = v.get_Parameter(BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC);
                            if (sp != null && !sp.IsReadOnly) sp.Set(opt.Scale);
                            else report.AppendLine($"⚠ {name}: scale is controlled by its view template (1:{v.Scale})");
                        }
                        newViews.Add(v);
                    }

                    if (opt.Mode == ViewDuplicateOption.AsDependent && opt.Scale > 0 && source.Scale != opt.Scale)
                    {
                        Parameter sp = source.get_Parameter(BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC);
                        if (sp != null && !sp.IsReadOnly) sp.Set(opt.Scale);
                        else report.AppendLine($"⚠ Dependent views follow the main view's scale (1:{source.Scale}), which its template controls.");
                    }

                    // --- 3b. Sheets ---
                    if (opt.PlaceOnSheets)
                    {
                        doc.Regenerate();
                        var sheetsByNumber = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                            .Cast<ViewSheet>().GroupBy(s => s.SheetNumber).ToDictionary(g => g.Key, g => g.First());

                        ViewSheet refSheet = opt.CopyFromSheet != null ? doc.GetElement(opt.CopyFromSheet) as ViewSheet : null;
                        Viewport refVp = refSheet?.GetAllViewports().Select(id => doc.GetElement(id) as Viewport).FirstOrDefault(x => x != null);
                        XYZ refOffset = null;   // viewport centre relative to the title block's lower-left corner
                        if (refVp != null)
                        {
                            BoundingBoxXYZ refTb = TitleBlockBox(doc, refSheet);
                            if (refTb != null) refOffset = refVp.GetBoxCenter() - refTb.Min;
                        }

                        for (int i = 0; i < newViews.Count; i++)
                        {
                            string number = opt.SheetNumbers[i], sname = opt.SheetNames[i];
                            View v = newViews[i];

                            if (!sheetsByNumber.TryGetValue(number, out ViewSheet sheet))
                            {
                                sheet = ViewSheet.Create(doc, opt.TitleBlockType ?? ElementId.InvalidElementId);
                                sheet.SheetNumber = number;
                                sheet.Name = sname;
                                if (refSheet != null) CopySheetData(doc, refSheet, sheet);
                                sheetsByNumber[number] = sheet;
                                madeSheets++;
                                doc.Regenerate();   // title block needs geometry before we measure it
                            }

                            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, v.Id))
                            {
                                report.AppendLine($"⚠ {v.Name}: already on a sheet (or not placeable) - skipped {number}");
                                continue;
                            }

                            BoundingBoxXYZ tb = TitleBlockBox(doc, sheet);
                            XYZ centre = tb == null ? XYZ.Zero
                                       : refOffset != null ? tb.Min + refOffset
                                       : (tb.Min + tb.Max) * 0.5;
                            centre = new XYZ(centre.X, centre.Y, 0);

                            Viewport vp = Viewport.Create(doc, sheet.Id, v.Id, centre);
                            ElementId vpType = opt.ViewportType ?? refVp?.GetTypeId();
                            if (vpType != null && vpType != ElementId.InvalidElementId) vp.ChangeTypeId(vpType);
                            vp.SetBoxCenter(centre);
                            placed++;
                        }
                    }

                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    message = ex.Message;
                    TaskDialog.Show("Views & Sheets", "Nothing was changed. Error:\n" + ex.Message);
                    return Result.Failed;
                }
            }

            string head = $"{madeViews} view(s) created" + (reusedViews > 0 ? $", {reusedViews} existing reused" : "");
            if (opt.PlaceOnSheets) head += $"\n{madeSheets} sheet(s) created, {placed} view(s) placed";
            if (resetCropShapes > 0)
                report.Insert(0, $"ℹ {resetCropShapes} view(s) inherited an edited (non-rectangular) crop region from the " +
                                 "main view; it was reset to rectangular so the scope box could be applied.\n");
            new TaskDialog("Views & Sheets")
            {
                MainInstruction = head,
                MainContent = report.Length == 0 ? "All done." : report.ToString(),
                FooterText = "Undo everything with Ctrl+Z"
            }.Show();
            return Result.Succeeded;
        }

        // =====================================================================
        /// <summary>
        /// Assigns a scope box to a view. Revit locks the Scope Box parameter when the view's crop region
        /// has an edited (non-rectangular) shape - a dependent view inherits that shape from its main view.
        /// In that case the shape is reset to a plain rectangle and the scope box is applied.
        /// Returns false with a reason if it still can't be set. 'note' is non-null when the shape was reset.
        /// </summary>
        private static bool TrySetScopeBox(Document doc, View v, ElementId scopeBoxId, out string note)
        {
            note = null;
            Parameter sbp = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
            if (sbp == null) { note = "view has no Scope Box parameter"; return false; }

            if (sbp.IsReadOnly)
            {
                ViewCropRegionShapeManager csm = v.GetCropRegionShapeManager();
                if (csm != null && csm.ShapeSet)
                {
                    csm.RemoveCropRegionShape();
                    doc.Regenerate();
                    sbp = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    note = "crop shape reset";
                }
            }

            if (sbp.IsReadOnly) { note = "parameter is locked; crop region is not an edited shape"; return false; }
            sbp.Set(scopeBoxId);
            return true;
        }

        private static View FindSourceView(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            foreach (ElementId id in uidoc.Selection.GetElementIds())
            {
                Element e = doc.GetElement(id);
                if (e is Viewport vp && doc.GetElement(vp.ViewId) is ViewPlan vpv) return Primary(doc, vpv);
                if (e is ViewPlan sel && !sel.IsTemplate) return Primary(doc, sel);
            }
            return doc.ActiveView is ViewPlan av && !av.IsTemplate ? Primary(doc, av) : null;
        }

        /// <summary>A dependent view can't be duplicated as dependent: use its main view instead.</summary>
        private static View Primary(Document doc, View v)
        {
            ElementId p = v.GetPrimaryViewId();
            return p != ElementId.InvalidElementId && doc.GetElement(p) is View pv ? pv : v;
        }

        private static BoundingBoxXYZ TitleBlockBox(Document doc, ViewSheet sheet)
        {
            Element tb = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();
            return tb?.get_BoundingBox(sheet);
        }

        /// <summary>Sheet parameters (Drawn By, browser-organisation fields...), revisions and title block values.</summary>
        private static void CopySheetData(Document doc, ViewSheet src, ViewSheet dst)
        {
            var skip = new HashSet<long>
            {
                (long)BuiltInParameter.SHEET_NAME, (long)BuiltInParameter.SHEET_NUMBER,
                (long)BuiltInParameter.VIEW_NAME
            };
            CopyParams(src, dst, skip);
            try { dst.SetAdditionalRevisionIds(src.GetAdditionalRevisionIds()); } catch { }

            Element a = new FilteredElementCollector(doc, src.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().FirstElement();
            Element b = new FilteredElementCollector(doc, dst.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType().FirstElement();
            if (a != null && b != null && a.GetTypeId() == b.GetTypeId())
                CopyParams(a, b, new HashSet<long> { (long)BuiltInParameter.SHEET_NAME, (long)BuiltInParameter.SHEET_NUMBER });
        }

        private static void CopyParams(Element src, Element dst, HashSet<long> skip)
        {
            foreach (Parameter p in src.Parameters)
            {
                try
                {
                    if (p.IsReadOnly || !p.HasValue) continue;
#if REVIT2024_OR_GREATER
                    if (skip.Contains(p.Id.Value)) continue;
#else
                    if (skip.Contains(p.Id.IntegerValue)) continue;
#endif
                    Parameter q = dst.get_Parameter(p.Definition);
                    if (q == null || q.IsReadOnly) continue;
                    switch (p.StorageType)
                    {
                        case StorageType.String: q.Set(p.AsString() ?? ""); break;
                        case StorageType.Integer: q.Set(p.AsInteger()); break;
                        case StorageType.Double: q.Set(p.AsDouble()); break;
                        case StorageType.ElementId: q.Set(p.AsElementId()); break;
                    }
                }
                catch { /* some parameters refuse values on a fresh sheet: ignore */ }
            }
        }
    }

    // =========================================================================
    //  Data + options
    // =========================================================================
    public class ScopeBoxItem
    {
        public ElementId Id;
        public string Name;
        public override string ToString() => Name;
    }

    public class NamedId
    {
        public ElementId Id;     // null = special entry ("keep", "none", ...)
        public string Name;
        public override string ToString() => Name;
    }

    public class ViewsSheetsData
    {
        public View Source;
        public List<ScopeBoxItem> ScopeBoxes = new List<ScopeBoxItem>();
        public List<NamedId> Templates = new List<NamedId>();
        public List<NamedId> TitleBlocks = new List<NamedId>();
        public List<NamedId> Sheets = new List<NamedId>();
        public List<NamedId> ViewportTypes = new List<NamedId>();
        public ElementId SourceScopeBox;

        public static ViewsSheetsData Load(Document doc, View source)
        {
            var d = new ViewsSheetsData { Source = source };

            d.ScopeBoxes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest)
                .WhereElementIsNotElementType()
                .Select(e => new ScopeBoxItem { Id = e.Id, Name = e.Name })
                .OrderBy(s => s.Name, NaturalComparer.Instance).ToList();

            d.Templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate && v.ViewType == source.ViewType)
                .Select(v => new NamedId { Id = v.Id, Name = v.Name })
                .OrderBy(n => n.Name).ToList();

            d.TitleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType().Cast<FamilySymbol>()
                .Select(f => new NamedId { Id = f.Id, Name = $"{f.FamilyName} : {f.Name}" })
                .OrderBy(n => n.Name).ToList();

            d.Sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .Select(s => new NamedId { Id = s.Id, Name = $"{s.SheetNumber} - {s.Name}" })
                .OrderBy(n => n.Name, NaturalComparer.Instance).ToList();

            // Viewport types = ElementTypes whose family is "Viewport"
            d.ViewportTypes = new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                .Where(t => t.FamilyName == "Viewport")
                .Select(t => new NamedId { Id = t.Id, Name = t.Name })
                .OrderBy(n => n.Name).ToList();

            Parameter p = source.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
            d.SourceScopeBox = p?.AsElementId();
            return d;
        }
    }

    public class ViewsSheetsOptions
    {
        public List<ScopeBoxItem> ScopeBoxes = new List<ScopeBoxItem>();
        public ViewDuplicateOption Mode = ViewDuplicateOption.Duplicate;
        public string NamePattern = "{View} - {ScopeBox}";
        public ElementId TemplateChoice;      // null = keep what the duplicate has
        public int Scale;                     // 0 = keep
        public bool PlaceOnSheets;
        public ElementId TitleBlockType;
        public ElementId CopyFromSheet;       // copy parameters/revisions/viewport position from this sheet
        public ElementId ViewportType;        // null = same as the copy-from sheet's viewport
        public List<string> SheetNumbers = new List<string>();
        public List<string> SheetNames = new List<string>();

        public string NameFor(string view, string scopeBox) =>
            NamePattern.Replace("{View}", view).Replace("{ScopeBox}", scopeBox)
                       .Replace("{N}", Series.LastNumber(scopeBox));
    }

    // =========================================================================
    //  Number series: "…PART 29 OF 41" -> "…PART 41 OF 41"
    // =========================================================================
    public static class Series
    {
        private static readonly Regex Num = new Regex(@"\d+");

        public static string LastNumber(string s)
        {
            MatchCollection m = Num.Matches(s ?? "");
            return m.Count > 0 ? m[m.Count - 1].Value : "";
        }

        /// <summary>
        /// Builds 'count' values from first to last. The ONE number that differs between first and last
        /// is the counter (keeps its zero padding); everything else is copied as is.
        /// Returns null and an error text if first/last don't make a series of that length.
        /// </summary>
        public static List<string> Build(string first, string last, int count, out string error)
        {
            error = null;
            first = (first ?? "").Trim();
            last = (last ?? "").Trim();
            if (count <= 0) { error = "Tick at least one scope box."; return null; }
            if (first.Length == 0) { error = "Enter the first value."; return null; }
            if (count == 1 && (last.Length == 0 || last == first)) return new List<string> { first };

            MatchCollection a = Num.Matches(first), b = Num.Matches(last);
            if (a.Count != b.Count || Num.Replace(first, "#") != Num.Replace(last, "#"))
            {
                error = $"'{first}' and '{last}' must be the same text with only one number changing.";
                return null;
            }

            int idx = -1;
            for (int i = 0; i < a.Count; i++)
                if (long.Parse(a[i].Value) != long.Parse(b[i].Value))
                {
                    if (idx >= 0) { error = $"More than one number changes between '{first}' and '{last}'."; return null; }
                    idx = i;
                }
            if (idx < 0) { error = $"First and last are the same, but {count} views are ticked."; return null; }

            long from = long.Parse(a[idx].Value), to = long.Parse(b[idx].Value);
            long n = Math.Abs(to - from) + 1;
            if (n != count)
            {
                error = $"'{first}' → '{last}' gives {n} value(s), but {count} scope box(es) are ticked.";
                return null;
            }

            int step = to >= from ? 1 : -1, width = a[idx].Value.Length;
            Match m = a[idx];
            var list = new List<string>();
            for (long k = 0; k < n; k++)
            {
                string value = (from + k * step).ToString().PadLeft(width, '0');
                list.Add(first.Substring(0, m.Index) + value + first.Substring(m.Index + m.Length));
            }
            return list;
        }
    }

    /// <summary>"OCC-SB-2" before "OCC-SB-10".</summary>
    public class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();
        private static readonly Regex Parts = new Regex(@"\d+|\D+");

        public int Compare(string x, string y)
        {
            MatchCollection a = Parts.Matches(x ?? ""), b = Parts.Matches(y ?? "");
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                string p = a[i].Value, q = b[i].Value;
                int c = char.IsDigit(p[0]) && char.IsDigit(q[0])
                    ? decimal.Parse(p).CompareTo(decimal.Parse(q))
                    : string.Compare(p, q, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return a.Count.CompareTo(b.Count);
        }
    }
}
