using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;

namespace PipeTagger
{
    /// <summary>
    /// "Tag Pipes / Ducts" button. MODIFIES THE MODEL: it ADDS tags (annotation).
    /// It never moves, changes or deletes pipes, ducts or existing tags.
    /// With "Add leader" + an arrowhead chosen, it also sets the Leader Arrowhead on the
    /// TAG TYPES used (a type parameter, so it affects every tag of those types).
    /// Everything happens in one transaction, so a single Ctrl+Z undoes the whole run.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class TagPipesCommand : IExternalCommand
    {
        private const double MmPerFoot = 304.8;   // Revit stores lengths in decimal feet

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                message = "Open a project first.";
                return Result.Failed;
            }

            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            // ---------- 1. Read systems + tag types for pipes and ducts ----------
            var data = new Dictionary<MepKind, MepData>
            {
                [MepKind.Pipes] = MepData.Load(doc, MepKind.Pipes),
                [MepKind.Ducts] = MepData.Load(doc, MepKind.Ducts)
            };
            List<ArrowItem> arrows = Arrowheads.Load(doc, out int defaultArrow);

            // ---------- 2. Selection / context ----------
            ICollection<ElementId> selIds = uidoc.Selection.GetElementIds();
            var selectedByKind = new Dictionary<MepKind, List<ElementId>>
            {
                [MepKind.Pipes] = selIds.Where(id => doc.GetElement(id) is Pipe).ToList(),
                [MepKind.Ducts] = selIds.Where(id => doc.GetElement(id) is Duct).ToList()
            };
            List<ViewSheet> selectedSheets = selIds.Select(id => doc.GetElement(id))
                                                   .OfType<ViewSheet>()
                                                   .ToList();

            // System Types of whatever is selected (pipes, ducts, fittings, accessories...)
            // -> the dialog opens with exactly these systems ticked
            var selectedSystems = new Dictionary<MepKind, HashSet<ElementId>>
            {
                [MepKind.Pipes] = new HashSet<ElementId>(),
                [MepKind.Ducts] = new HashSet<ElementId>()
            };
            foreach (ElementId id in selIds)
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;
                foreach (MepKind k in new[] { MepKind.Pipes, MepKind.Ducts })
                {
                    Parameter p = e.get_Parameter(MepData.SystemParam(k));
                    if (p == null || p.StorageType != StorageType.ElementId) continue;
                    ElementId sys = p.AsElementId();
                    // A pipe/duct with no system still counts ("<No system>" row)
                    bool isRun = k == MepKind.Pipes ? e is Pipe : e is Duct;
                    if (sys != ElementId.InvalidElementId || isRun)
                        selectedSystems[k].Add(sys);
                }
            }

            bool activeIsSheet = activeView is ViewSheet;
            bool activeIsTaggable = IsTaggableView(activeView);

            // ---------- 3. Options dialog ----------
            TagOptions options;
            using (var form = new TagOptionsForm(data, activeView.Name, activeIsTaggable, activeIsSheet,
                                                 selectedSheets.Count,
                                                 selectedByKind[MepKind.Pipes].Count,
                                                 selectedByKind[MepKind.Ducts].Count,
                                                 selectedSystems, arrows, defaultArrow))
            {
                if (form.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return Result.Cancelled;
                options = form.Options;
            }

            // ---------- 4. Views to tag ----------
            var targets = new List<View>();
            ICollection<ElementId> onlyTheseElements = null;

            switch (options.Scope)
            {
                case TagScope.ActiveView:
                    targets.Add(activeView);
                    break;
                case TagScope.SelectedElements:
                    // In a view: tag there. On a sheet: tag in every view of the sheet that shows them.
                    if (activeIsTaggable) targets.Add(activeView);
                    else if (activeIsSheet) targets.AddRange(ViewsOnSheet(doc, (ViewSheet)activeView));
                    onlyTheseElements = selectedByKind[options.Kind];
                    break;
                case TagScope.ActiveSheet:
                    targets.AddRange(ViewsOnSheet(doc, (ViewSheet)activeView));
                    break;
                case TagScope.SelectedSheets:
                    foreach (ViewSheet sheet in selectedSheets)
                        targets.AddRange(ViewsOnSheet(doc, sheet));
                    break;
            }
            targets = targets.GroupBy(v => v.Id).Select(g => g.First()).ToList();

            if (targets.Count == 0)
            {
                TaskDialog.Show("MEP Tagger",
                    "No views that can hold tags were found.\n" +
                    "(Plans, sections, elevations, details and locked 3D views are supported.)");
                return Result.Cancelled;
            }

            // ---------- 5. Place the tags (one transaction = one Undo) ----------
            var report = new StringBuilder();
            var total = new TagStats();
            LeaderPlacer.ResetStats();

            using (var t = new Transaction(doc, "Tag " + options.Kind))
            {
                t.Start();
                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(fho);

                try
                {
                    // Leader arrowhead is a TYPE parameter of the tag family: set it on the tag types used
                    if (options.AddLeader && options.Arrow != null && (options.Arrow.Id != null || options.Arrow.CreateNew))
                    {
                        ElementId arrowId = options.Arrow.CreateNew ? Arrowheads.Create30(doc) : options.Arrow.Id;
                        if (arrowId != null)
                        {
                            int changed = Arrowheads.ApplyTo(doc, options.SystemToTag.Values, arrowId);
                            report.AppendLine($"Arrowhead: {doc.GetElement(arrowId).Name}" +
                                              (changed > 0 ? $" (set on {changed} tag type(s))" : ""));
                        }
                        else
                        {
                            report.AppendLine("Arrowhead: no arrow-style arrowhead found to copy, kept the tag's own.");
                        }
                    }

                    foreach (View view in targets)
                    {
                        TagStats stats = TagInView(doc, view, options, onlyTheseElements);
                        total.Add(stats);
                        report.AppendLine($"• {view.Name}: {stats.Tagged} tagged" + stats.SkipSummary());
                    }
                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    message = ex.Message;
                    TaskDialog.Show("MEP Tagger", "Nothing was changed. Error:\n" + ex.Message);
                    return Result.Failed;
                }
            }

            // ---------- 6. Summary ----------
            string kindName = options.Kind == MepKind.Pipes ? "pipe" : "duct";
            if (options.AddLeader)
            {
                report.AppendLine();
                report.AppendLine($"Text parallel to run: {LeaderPlacer.RotatedByProperty} by Angle, " +
                                  $"{LeaderPlacer.RotatedByTransform} by Rotate, {LeaderPlacer.RotateFailed} failed");
                if (LeaderPlacer.RotateFailed > 0)
                    report.AppendLine("⚠ " + LeaderPlacer.RotateError);
            }
            new TaskDialog("MEP Tagger")
            {
                MainInstruction = $"{total.Tagged} {kindName} tag(s) placed in {targets.Count} view(s)",
                MainContent = report.ToString(),
                FooterText = "Undo everything with Ctrl+Z"
            }.Show();

            return Result.Succeeded;
        }

        // =====================================================================
        //  Tagging logic for one view
        // =====================================================================
        private static TagStats TagInView(Document doc, View view, TagOptions opt,
                                          ICollection<ElementId> onlyThese)
        {
            var stats = new TagStats();

            // Pipes or ducts VISIBLE in this view (respects crop region, filters, hidden elements)
            IEnumerable<MEPCurve> items = new FilteredElementCollector(doc, view.Id)
                .OfCategory(MepData.CurveCategory(opt.Kind))
                .WhereElementIsNotElementType()
                .OfType<MEPCurve>();

            // "Selected only": keep just the selected ones that are visible in this view
            if (onlyThese != null)
            {
                var wanted = new HashSet<ElementId>(onlyThese);
                items = items.Where(e => wanted.Contains(e.Id));
            }

            // Which tag TYPES already sit on which elements in this view.
            // We only skip an element if it already has the SAME tag type
            // (so a pipe with a flow tag can still get a size tag).
            var existing = new Dictionary<ElementId, HashSet<ElementId>>();
            if (opt.SkipTagged)
            {
                foreach (IndependentTag tag in new FilteredElementCollector(doc, view.Id)
                             .OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                {
                    ElementId tagType = tag.GetTypeId();
#if REVIT2022_OR_GREATER
                    // Revit 2022+: a tag can point at several elements
                    foreach (ElementId id in tag.GetTaggedLocalElementIds())
#else
                    // Revit 2021: one element per tag
                    foreach (ElementId id in new[] { tag.TaggedLocalElementId })
#endif
                    {
                        if (!existing.TryGetValue(id, out var set))
                            existing[id] = set = new HashSet<ElementId>();
                        set.Add(tagType);
                    }
                }
            }

            // User values are in mm -> convert to Revit feet
            double minLenFt = opt.MinLengthMm / MmPerFoot;
            double maxLenFt = opt.MaxLengthMm > 0 ? opt.MaxLengthMm / MmPerFoot : double.MaxValue;
            // Offset is in PAPER mm -> multiply by view scale (3 mm at 1:100 = 300 mm in model)
            double offsetFt = opt.OffsetMm / MmPerFoot * Math.Max(1, view.Scale);

            XYZ right = view.RightDirection;
            XYZ up = view.UpDirection;

            // Tags created in this view (measured and moved after all are created)
            var placed = new List<PlacedTag>();

            foreach (MEPCurve el in items)
            {
                // ---- System filter: only the systems the user ticked ----
                ElementId sysType = MepData.SystemTypeOf(el, opt.Kind);
                if (!opt.SystemToTag.TryGetValue(sysType, out ElementId tagTypeId))
                {
                    stats.OtherSystem++;
                    continue;
                }

                if (existing.TryGetValue(el.Id, out var have) && have.Contains(tagTypeId))
                {
                    stats.AlreadyTagged++;
                    continue;
                }

                if (!(el.Location is LocationCurve lc) || lc.Curve == null) { stats.Failed++; continue; }

                Curve curve = lc.Curve;
                XYZ dir = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
                XYZ mid = curve.Evaluate(0.5, true);

                // Direction as it appears on paper
                double dx = dir.DotProduct(right);
                double dy = dir.DotProduct(up);
                double onPaper = Math.Sqrt(dx * dx + dy * dy);   // 1 = lies flat in view, 0 = points at you
                bool isRiser = onPaper < 0.2;

                if (isRiser && opt.SkipRisers) { stats.Risers++; continue; }

                // ---- Length filter (real 3D length of the segment) ----
                double len = curve.Length;
                if (len < minLenFt) { stats.TooShort++; continue; }
                if (len > maxLenFt) { stats.TooLong++; continue; }

                // ---- Orientation + where the tag head goes ----
                TagOrientation orientation;
                XYZ offsetDir;
                if (isRiser)
                {
                    orientation = TagOrientation.Horizontal;
                    offsetDir = (right + up).Normalize();
                }
                else
                {
                    orientation = Math.Abs(dy) > Math.Abs(dx)
                        ? TagOrientation.Vertical      // runs up/down the sheet
                        : TagOrientation.Horizontal;   // runs left/right

                    // Offset at 90° to the run AS SEEN ON PAPER (works for sloped/diagonal runs too),
                    // pointing up the sheet, or to the left for near-vertical runs
                    offsetDir = (right * -dy + up * dx).Normalize();
                    bool flip = orientation == TagOrientation.Vertical
                        ? offsetDir.DotProduct(right) > 0
                        : offsetDir.DotProduct(up) < 0;
                    if (flip) offsetDir = offsetDir.Negate();
                }

                XYZ target = mid + offsetDir * offsetFt;   // where the CENTRE of the text should end up

                // Run direction and its perpendicular on paper (for the leader placement search)
                var normal = new P2(offsetDir.DotProduct(right), offsetDir.DotProduct(up));
                double nl = Math.Sqrt(normal.U * normal.U + normal.V * normal.V);
                normal = nl > 1e-9 ? normal * (1 / nl) : new P2(0, 1);
                P2 along = isRiser ? new P2(-normal.V, normal.U) : new P2(dx / onPaper, dy / onPaper);

                try
                {
                    // Always created WITHOUT a leader: the leader would distort the measured text box.
                    // It is added after the tag has been moved to a free spot.
                    IndependentTag tag = IndependentTag.Create(doc, tagTypeId, view.Id, new Reference(el),
                                                               false, orientation, target);
                    placed.Add(new PlacedTag
                    {
                        Tag = tag, Element = el, Mid = mid, Target = target,
                        Dir3 = dir, Dir = along, Normal = normal,
                        OnPaper = onPaper, PaperLength = curve.Length * onPaper,
                        IsRiser = isRiser, CreatedVertical = orientation == TagOrientation.Vertical,
                        HalfWidth = LeaderPlacer.HalfWidth(el, view)
                    });
                    stats.Tagged++;
                }
                catch
                {
                    stats.Failed++;
                }
            }

            if (placed.Count == 0) return stats;

            if (opt.AddLeader)
            {
                // ---- With leader: free spot next to the run, arrow at the exact middle ----
                doc.Regenerate();   // tags need geometry before they can be measured
                LeaderPlacer.Place(doc, view, placed, opt.OffsetMm);
            }
            else if (opt.CenterOnElement)
            {
                // ---- Exact centring ----
                // The head point Revit uses is wherever the tag family's origin is, which is often
                // the corner or baseline of the label, not its middle. So: measure each tag's real
                // box, and slide the tag until the middle of its text sits exactly on the target
                // (centre of the run along its length, and on the centreline across its width).
                doc.Regenerate();
                XYZ viewDir = view.ViewDirection;

                foreach (PlacedTag p in placed)
                {
                    BoundingBoxXYZ bb = p.Tag.get_BoundingBox(view);
                    if (bb == null) continue;

                    XYZ centre = bb.Transform.OfPoint((bb.Min + bb.Max) * 0.5);
                    XYZ delta = p.Target - centre;
                    delta -= viewDir * delta.DotProduct(viewDir);   // move only within the paper plane

                    if (delta.GetLength() > 1e-6)
                        p.Tag.TagHeadPosition = p.Tag.TagHeadPosition + delta;
                }
            }

            return stats;
        }

        // =====================================================================
        //  Helpers
        // =====================================================================
        private static IEnumerable<View> ViewsOnSheet(Document doc, ViewSheet sheet) =>
            sheet.GetAllPlacedViews()
                 .Select(id => doc.GetElement(id) as View)
                 .Where(IsTaggableView);

        private static bool IsTaggableView(View v)
        {
            if (v == null || v.IsTemplate) return false;
            switch (v.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                    return true;
                case ViewType.ThreeD:
                    return v is View3D v3 && v3.IsLocked;
                default:
                    return false;
            }
        }
    }

    // =========================================================================
    //  Model data shown in the dialog
    // =========================================================================
    public enum MepKind { Pipes, Ducts }

    public class SystemRow
    {
        public ElementId Id;     // InvalidElementId = elements with no system
        public string Name;
        public int Count;        // how many pipes/ducts in the whole model use it
    }

    public class TagTypeItem
    {
        public ElementId Id;
        public string Name;
        public bool IsMultiCategory;
    }

    public class MepData
    {
        public List<SystemRow> Systems = new List<SystemRow>();
        public List<TagTypeItem> TagTypes = new List<TagTypeItem>();

        public static BuiltInCategory CurveCategory(MepKind k) =>
            k == MepKind.Pipes ? BuiltInCategory.OST_PipeCurves : BuiltInCategory.OST_DuctCurves;

        public static BuiltInParameter SystemParam(MepKind k) =>
            k == MepKind.Pipes ? BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM
                               : BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM;

        public static ElementId SystemTypeOf(Element e, MepKind k)
        {
            Parameter p = e.get_Parameter(SystemParam(k));
            return p?.AsElementId() ?? ElementId.InvalidElementId;
        }

        public static MepData Load(Document doc, MepKind kind)
        {
            var data = new MepData();

            // Count elements per system type
            var counts = new Dictionary<ElementId, int>();
            foreach (Element e in new FilteredElementCollector(doc)
                         .OfCategory(CurveCategory(kind)).WhereElementIsNotElementType())
            {
                ElementId s = SystemTypeOf(e, kind);
                counts[s] = counts.TryGetValue(s, out int c) ? c + 1 : 1;
            }

            // All system types (Domestic Cold Water, Supply Air, ...)
            Type sysClass = kind == MepKind.Pipes ? typeof(PipingSystemType) : typeof(MechanicalSystemType);
            foreach (Element st in new FilteredElementCollector(doc).OfClass(sysClass))
            {
                counts.TryGetValue(st.Id, out int c);
                data.Systems.Add(new SystemRow { Id = st.Id, Name = st.Name, Count = c });
            }
            data.Systems = data.Systems.OrderByDescending(s => s.Count > 0).ThenBy(s => s.Name).ToList();

            if (counts.TryGetValue(ElementId.InvalidElementId, out int none) && none > 0)
                data.Systems.Add(new SystemRow { Id = ElementId.InvalidElementId, Name = "<No system>", Count = none });

            // Tag types: Pipe Tags / Duct Tags + Multi-Category tags
            BuiltInCategory tagCat = kind == MepKind.Pipes ? BuiltInCategory.OST_PipeTags : BuiltInCategory.OST_DuctTags;
            foreach (BuiltInCategory cat in new[] { tagCat, BuiltInCategory.OST_MultiCategoryTags })
            {
                foreach (FamilySymbol fs in new FilteredElementCollector(doc).OfCategory(cat)
                             .WhereElementIsElementType().Cast<FamilySymbol>()
                             .OrderBy(f => f.FamilyName).ThenBy(f => f.Name))
                {
                    data.TagTypes.Add(new TagTypeItem
                    {
                        Id = fs.Id,
                        Name = $"{fs.FamilyName} : {fs.Name}",
                        IsMultiCategory = cat == BuiltInCategory.OST_MultiCategoryTags
                    });
                }
            }

            return data;
        }
    }

    // =========================================================================
    //  Counters + warning handler
    // =========================================================================
    internal class TagStats
    {
        public int Tagged, AlreadyTagged, TooShort, TooLong, Risers, OtherSystem, Failed;

        public void Add(TagStats o)
        {
            Tagged += o.Tagged; AlreadyTagged += o.AlreadyTagged; TooShort += o.TooShort;
            TooLong += o.TooLong; Risers += o.Risers; OtherSystem += o.OtherSystem; Failed += o.Failed;
        }

        public string SkipSummary()
        {
            var parts = new List<string>();
            if (AlreadyTagged > 0) parts.Add($"{AlreadyTagged} already tagged");
            if (TooShort > 0) parts.Add($"{TooShort} too short");
            if (TooLong > 0) parts.Add($"{TooLong} too long");
            if (Risers > 0) parts.Add($"{Risers} risers");
            if (Failed > 0) parts.Add($"{Failed} failed");
            return parts.Count == 0 ? "" : " (skipped: " + string.Join(", ", parts) + ")";
        }
    }

    internal class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (FailureMessageAccessor f in accessor.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning)
                    accessor.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}
