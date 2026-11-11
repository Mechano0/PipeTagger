using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace PipeTagger
{
    /// <summary>A tag we just created + what it belongs to.</summary>
    internal class PlacedTag
    {
        public IndependentTag Tag;
        public MEPCurve Element;
        public XYZ Mid;            // middle of the run (3D) -> arrow tip
        public XYZ Target;         // where the text centre should go when centring without leader
        public XYZ Dir3;           // run direction in the model (unit)
        public P2 Dir;             // run direction on paper (unit)
        public P2 Normal;          // 90° to the run on paper, pointing up / left (unit)
        public double OnPaper;     // 1 = run lies flat in the view, ~0 = seen end-on
        public double PaperLength; // length of the run as drawn (feet)
        public double HalfWidth;   // half the visible width of the run (feet)
        public bool IsRiser;
        public bool CreatedVertical;
    }

    // =========================================================================
    //  Leader placement:
    //   - text turned parallel to the run
    //   - arrow tip exactly on the middle of the run
    //   - leader one straight line, at 90° to the run
    //   - tag in the closest spot that doesn't cover other elements
    // =========================================================================
    internal static class LeaderPlacer
    {
        private const double MmPerFoot = 304.8;

        /// <summary>Rotation results for the summary window.</summary>
        public static int RotatedByProperty, RotatedByTransform, RotateFailed;
        public static string RotateError;

        public static void ResetStats()
        {
            RotatedByProperty = RotatedByTransform = RotateFailed = 0;
            RotateError = null;
        }

        /// <param name="clearanceMm">gap between run edge and tag text, mm on paper</param>
        public static void Place(Document doc, View view, List<PlacedTag> placed, double clearanceMm)
        {
            XYZ right = view.RightDirection, up = view.UpDirection;
            double paperMm = Math.Max(1, view.Scale) / MmPerFoot;   // 1 mm on paper, in model feet
            double clearance = clearanceMm * paperMm;
            double pad = 1.0 * paperMm;                             // 1 mm breathing room

            // ---- 1. Text size before rotating: length along the text, height across it ----
            var size = new Dictionary<PlacedTag, (double Len, double Hgt)>();
            foreach (PlacedTag p in placed)
            {
                BoundingBoxXYZ bb = p.Tag.get_BoundingBox(view);
                if (bb == null) continue;
                Rect2 r = Rect2.FromBox(bb, right, up);
                size[p] = p.CreatedVertical ? (r.Height, r.Width) : (r.Width, r.Height);
            }

            // ---- 2. Turn the text parallel to its run ----
            // a) the official way (Orientation = Model + Angle)
            foreach (PlacedTag p in placed) RotateParallel(p);
            doc.Regenerate();

            // b) check the DRAWN text really turned (Revit can accept the angle but not draw it);
            //    if not, rotate the tag around its head point like the Rotate tool does
            var needTransform = placed.Where(p => size.ContainsKey(p) && !LooksRotated(p, view, size[p])).ToList();
            foreach (PlacedTag p in needTransform) RotateByTransform(doc, view, p);
            if (needTransform.Count > 0) doc.Regenerate();

            // c) count what worked
            foreach (PlacedTag p in placed)
            {
                if (p.IsRiser || !size.ContainsKey(p) || Math.Abs(RunAngle(p)) < Deg(1)) continue;
                if (!LooksRotated(p, view, size[p])) { RotateFailed++; if (RotateError == null) RotateError = Describe(p); }
                else if (needTransform.Contains(p)) RotatedByTransform++;
                else RotatedByProperty++;
            }

            // ---- 3. Everything already in the view that the tags must not cover ----
            var ignore = new HashSet<ElementId>(placed.Select(p => p.Tag.Id));
            Obstacles obstacles = Obstacles.Collect(doc, view, ignore);

            foreach (PlacedTag p in placed)
            {
                BoundingBoxXYZ bb = p.Tag.get_BoundingBox(view);
                if (bb == null || !size.TryGetValue(p, out var s))
                {
                    AddStraightLeader(p, p.Mid);
                    continue;
                }

                P2 d = p.Dir, n = p.Normal;
                P2 mid = P2.Of(p.Mid, right, up);
                P2 head = P2.Of(p.Tag.TagHeadPosition, right, up);
                P2 textOffset = Rect2.FromBox(bb, right, up).Center - head;   // text centre relative to head point

                // Text box (as rotated) and how far it reaches across the run
                double across = s.Hgt / 2;
                double boxW = Math.Abs(d.U) * s.Len + Math.Abs(n.U) * s.Hgt;
                double boxH = Math.Abs(d.V) * s.Len + Math.Abs(n.V) * s.Hgt;
                double ringStep = Math.Max(s.Hgt, 3 * paperMm);

                // Slides along the run are a last resort (the arrow then moves off the exact middle).
                // They stay inside the run so the arrow always lands on it.
                double slideStep = s.Len + pad;
                double maxSlide = Math.Max(0, p.PaperLength / 2 - 2 * paperMm);
                var slides = new List<double> { 0 };
                foreach (int k in new[] { 1, -1, 2, -2 })
                    if (Math.Abs(k * slideStep) <= maxSlide) slides.Add(k * slideStep);

                P2? bestHead = null, firstHead = null;
                double bestSlide = 0;

                foreach (double slide in slides)
                {
                    for (int ring = 0; ring < 8 && bestHead == null; ring++)
                    {
                        double dist = p.HalfWidth + clearance + across + ring * ringStep;
                        foreach (int side in new[] { 1, -1 })
                        {
                            // Head point on the perpendicular through the arrow tip,
                            // so the leader is one straight line at 90° to the run
                            P2 foot = mid + d * slide;
                            double headAcross = side * dist - textOffset.Dot(n);
                            P2 h = foot + n * headAcross;
                            P2 centre = h + textOffset;

                            if (firstHead == null) firstHead = h;
                            if (!obstacles.Hits(Rect2.Around(centre, boxW + 2 * pad, boxH + 2 * pad)))
                            {
                                bestHead = h;
                                bestSlide = slide;
                                break;
                            }
                        }
                    }
                    if (bestHead != null) break;
                }

                P2 chosen = bestHead ?? firstHead.Value;   // nothing free: closest spot
                P2 move = chosen - head;
                p.Tag.TagHeadPosition = p.Tag.TagHeadPosition + right * move.U + up * move.V;
                obstacles.AddBox(Rect2.Around(chosen + textOffset, boxW, boxH));

                // Arrow tip: middle of the run (or slid along the centreline if it had to move)
                XYZ tip = p.Mid;
                if (Math.Abs(bestSlide) > 1e-9 && p.OnPaper > 1e-6)
                    tip = p.Mid + p.Dir3 * (bestSlide / p.OnPaper);

                AddStraightLeader(p, tip);
            }
        }

        private static double Deg(double d) => d * Math.PI / 180;

        /// <summary>
        /// True when the text as DRAWN is at the run's angle. Compares the tag's real paper box with
        /// the box the text would have turned (angle a) vs not turned (horizontal / vertical as created).
        /// </summary>
        private static bool LooksRotated(PlacedTag p, View view, (double Len, double Hgt) s)
        {
            double a = RunAngle(p);
            if (p.IsRiser || Math.Abs(a) < Deg(1)) return true;

            BoundingBoxXYZ bb = p.Tag.get_BoundingBox(view);
            if (bb == null) return true;
            Rect2 r = Rect2.FromBox(bb, view.RightDirection, view.UpDirection);

            double sin = Math.Abs(Math.Sin(a)), cos = Math.Abs(Math.Cos(a));
            double turnedW = cos * s.Len + sin * s.Hgt, turnedH = sin * s.Len + cos * s.Hgt;
            double flatW = p.CreatedVertical ? s.Hgt : s.Len, flatH = p.CreatedVertical ? s.Len : s.Hgt;

            double errTurned = Math.Abs(r.Width - turnedW) + Math.Abs(r.Height - turnedH);
            double errFlat = Math.Abs(r.Width - flatW) + Math.Abs(r.Height - flatH);
            return errTurned <= errFlat;
        }

        /// <summary>Rotate the tag around its head point, in the paper plane (same as the Rotate tool).</summary>
        private static void RotateByTransform(Document doc, View view, PlacedTag p)
        {
            try
            {
                double drawn = p.CreatedVertical ? Math.PI / 2 : 0;   // what is on paper now
                double turn = RunAngle(p) - drawn;
                if (Math.Abs(turn) < 1e-6) return;
                XYZ head = p.Tag.TagHeadPosition;
                Line axis = Line.CreateBound(head, head + view.ViewDirection);
                ElementTransformUtils.RotateElement(doc, p.Tag.Id, axis, turn);
            }
            catch (Exception ex)
            {
                if (RotateError == null) RotateError = "Rotate: " + ex.Message;
            }
        }

        private static string Describe(PlacedTag p)
        {
            string s = $"wanted {RunAngle(p) * 180 / Math.PI:0.#}°";
#if REVIT2022_OR_GREATER
            try { s += $", Revit reports Orientation={p.Tag.TagOrientation}, Angle={p.Tag.RotationAngle * 180 / Math.PI:0.#}°"; }
            catch { }
#endif
            return RotateError == null ? s : s + " | " + RotateError;
        }

        /// <summary>Readable angle of the run on paper: -90° &lt; angle &lt;= 90°.</summary>
        private static double RunAngle(PlacedTag p)
        {
            double angle = Math.Atan2(p.Dir.V, p.Dir.U);
            if (angle > Math.PI / 2 + 1e-9) angle -= Math.PI;
            if (angle <= -Math.PI / 2 + 1e-9) angle += Math.PI;
            return angle;
        }


        /// <summary>Revit 2022+: rotate the text to the run's angle (kept readable). Revit 2021 keeps H/V.</summary>
        private static void RotateParallel(PlacedTag p)
        {
#if REVIT2022_OR_GREATER
            if (p.IsRiser) return;   // seen end-on: keep horizontal
            try
            {
                if (p.Tag.TagOrientation != TagOrientation.AnyModelDirection)
                    p.Tag.TagOrientation = TagOrientation.AnyModelDirection;
                p.Tag.RotationAngle = RunAngle(p);   // never upside down
            }
            catch (Exception ex)
            {
                // Remember why (shown in the summary); VerifyRotation counts it
                if (RotateError == null) RotateError = ex.Message;
            }
#endif
        }

        /// <summary>Free leader ending at <paramref name="tip"/>, drawn as one straight line (no bend).</summary>
        private static void AddStraightLeader(PlacedTag p, XYZ tip)
        {
            try
            {
                p.Tag.HasLeader = true;
                p.Tag.LeaderEndCondition = LeaderEndCondition.Free;
                XYZ elbow = (p.Tag.TagHeadPosition + tip) * 0.5;   // on the line -> no visible bend
#if REVIT2022_OR_GREATER
                var r = new Reference(p.Element);
                p.Tag.SetLeaderEnd(r, tip);
                p.Tag.SetLeaderElbow(r, elbow);
#else
                p.Tag.LeaderEnd = tip;
                p.Tag.LeaderElbow = elbow;
#endif
            }
            catch
            {
                // Some tag families don't allow free leader ends: keep Revit's default leader
            }
        }

        /// <summary>Half of the run's visible width in this view (feet).</summary>
        public static double HalfWidth(MEPCurve e, View view)
        {
            double w = 0, h = 0;
            try { w = e.Width; h = e.Height; } catch { }
            if (w <= 0) { try { w = h = e.Diameter; } catch { } }
            if (e is Pipe)
            {
                Parameter od = e.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
                if (od != null && od.AsDouble() > 0) w = h = od.AsDouble();
            }
            bool plan = Math.Abs(view.ViewDirection.Z) > 0.9;   // plans show the width
            return (plan ? w : Math.Max(w, h)) / 2;
        }
    }

    // =========================================================================
    //  What a tag must not sit on, in 2D paper coordinates
    // =========================================================================
    internal class Obstacles
    {
        private readonly List<Rect2> _boxes = new List<Rect2>();
        private readonly List<(P2 A, P2 B, double R, Rect2 Bounds)> _runs = new List<(P2, P2, double, Rect2)>();

        private static readonly BuiltInCategory[] Categories =
        {
            BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_FlexPipeCurves,
            BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_MechanicalEquipment,
            BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers,
            BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting
        };

        public static Obstacles Collect(Document doc, View view, ICollection<ElementId> ignore)
        {
            var o = new Obstacles();
            XYZ right = view.RightDirection, up = view.UpDirection;

            // MEP elements: straight runs as thick lines (accurate for diagonal ducts), the rest as boxes
            foreach (Element e in new FilteredElementCollector(doc, view.Id)
                         .WherePasses(new ElementMulticategoryFilter(Categories))
                         .WhereElementIsNotElementType())
            {
                if (e is MEPCurve run && run.Location is LocationCurve lc && lc.Curve is Line line)
                {
                    o.AddRun(P2.Of(line.GetEndPoint(0), right, up), P2.Of(line.GetEndPoint(1), right, up),
                             LeaderPlacer.HalfWidth(run, view));
                }
                else
                {
                    BoundingBoxXYZ bb = e.get_BoundingBox(view);
                    if (bb != null) o.AddBox(Rect2.FromBox(bb, right, up));
                }
            }

            // Existing tags and text notes
            foreach (Element e in new FilteredElementCollector(doc, view.Id)
                         .WherePasses(new LogicalOrFilter(new ElementClassFilter(typeof(IndependentTag)),
                                                          new ElementClassFilter(typeof(TextNote)))))
            {
                if (ignore.Contains(e.Id)) continue;
                BoundingBoxXYZ bb = e.get_BoundingBox(view);
                if (bb != null) o.AddBox(Rect2.FromBox(bb, right, up));
            }

            return o;
        }

        public void AddBox(Rect2 r) => _boxes.Add(r);

        public void AddRun(P2 a, P2 b, double radius)
        {
            var bounds = new Rect2(Math.Min(a.U, b.U), Math.Min(a.V, b.V), Math.Max(a.U, b.U), Math.Max(a.V, b.V))
                         .Inflate(radius);
            _runs.Add((a, b, radius, bounds));
        }

        public bool Hits(Rect2 r)
        {
            foreach (Rect2 b in _boxes)
                if (b.Intersects(r)) return true;
            foreach (var run in _runs)
                if (run.Bounds.Intersects(r) && SegmentHitsRect(run.A, run.B, r.Inflate(run.R)))
                    return true;
            return false;
        }

        /// <summary>Liang–Barsky: does segment a-b pass through rectangle r?</summary>
        private static bool SegmentHitsRect(P2 a, P2 b, Rect2 r)
        {
            double t0 = 0, t1 = 1, dx = b.U - a.U, dy = b.V - a.V;

            bool Clip(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;
                double t = q / p;
                if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
                return true;
            }

            return Clip(-dx, a.U - r.MinU) && Clip(dx, r.MaxU - a.U)
                && Clip(-dy, a.V - r.MinV) && Clip(dy, r.MaxV - a.V);
        }
    }

    // =========================================================================
    //  Tiny 2D helpers (U = view right, V = view up)
    // =========================================================================
    internal struct P2
    {
        public double U, V;
        public P2(double u, double v) { U = u; V = v; }
        public static P2 Of(XYZ p, XYZ right, XYZ up) => new P2(p.DotProduct(right), p.DotProduct(up));
        public double Dot(P2 o) => U * o.U + V * o.V;
        public static P2 operator +(P2 a, P2 b) => new P2(a.U + b.U, a.V + b.V);
        public static P2 operator -(P2 a, P2 b) => new P2(a.U - b.U, a.V - b.V);
        public static P2 operator *(P2 a, double s) => new P2(a.U * s, a.V * s);
    }

    internal struct Rect2
    {
        public double MinU, MinV, MaxU, MaxV;
        public Rect2(double minU, double minV, double maxU, double maxV) { MinU = minU; MinV = minV; MaxU = maxU; MaxV = maxV; }

        public double Width => MaxU - MinU;
        public double Height => MaxV - MinV;
        public P2 Center => new P2((MinU + MaxU) / 2, (MinV + MaxV) / 2);

        public static Rect2 Around(P2 c, double w, double h) => new Rect2(c.U - w / 2, c.V - h / 2, c.U + w / 2, c.V + h / 2);
        public Rect2 Inflate(double r) => new Rect2(MinU - r, MinV - r, MaxU + r, MaxV + r);
        public bool Intersects(Rect2 o) => MinU < o.MaxU && o.MinU < MaxU && MinV < o.MaxV && o.MinV < MaxV;

        /// <summary>Paper-space box of a 3D bounding box (all 8 corners projected).</summary>
        public static Rect2 FromBox(BoundingBoxXYZ bb, XYZ right, XYZ up)
        {
            double minU = double.MaxValue, minV = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue;
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
            foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
            {
                XYZ p = bb.Transform.OfPoint(new XYZ(x, y, z));
                double u = p.DotProduct(right), v = p.DotProduct(up);
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            return new Rect2(minU, minV, maxU, maxV);
        }
    }

    // =========================================================================
    //  Arrowheads (type parameter of the tag family)
    // =========================================================================
    public class ArrowItem
    {
        public ElementId Id;        // null = keep the tag type's own arrowhead
        public string Name;
        public bool CreateNew;      // "Arrow Filled 30 Degree" doesn't exist yet -> create it
        public override string ToString() => Name;
    }

    internal static class Arrowheads
    {
        private const double Deg30 = Math.PI / 6;
        public const string NewName = "Arrow Filled 30 Degree";

        private static List<ElementType> All(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                .Where(t => t.get_Parameter(BuiltInParameter.ARROW_TYPE) != null)
                .OrderBy(t => t.Name)
                .ToList();

        private static bool Is30(ElementType t)
        {
            Parameter a = t.get_Parameter(BuiltInParameter.LEADER_ARROW_WIDTH);
            return a != null && Math.Abs(a.AsDouble() - Deg30) < 1e-3;
        }

        private static bool IsFilled(ElementType t) =>
            t.get_Parameter(BuiltInParameter.ARROW_FILLED)?.AsInteger() == 1;

        /// <summary>All arrowheads in the project; the default is a 30° one (filled preferred).</summary>
        public static List<ArrowItem> Load(Document doc, out int defaultIndex)
        {
            List<ElementType> types = All(doc);
            var list = new List<ArrowItem> { new ArrowItem { Name = "(keep the tag type's arrowhead)" } };
            list.AddRange(types.Select(t => new ArrowItem { Id = t.Id, Name = t.Name }));

            ElementType pick = types.FirstOrDefault(t => Is30(t) && IsFilled(t))
                            ?? types.FirstOrDefault(Is30);
            if (pick != null)
            {
                defaultIndex = list.FindIndex(i => i.Id == pick.Id);
            }
            else
            {
                list.Add(new ArrowItem { Name = NewName + "  (will be created)", CreateNew = true });
                defaultIndex = list.Count - 1;
            }
            return list;
        }

        /// <summary>Creates "Arrow Filled 30 Degree" by copying an existing arrow-style arrowhead.</summary>
        public static ElementId Create30(Document doc)
        {
            ElementType template = All(doc).FirstOrDefault(t =>
            {
                Parameter a = t.get_Parameter(BuiltInParameter.LEADER_ARROW_WIDTH);
                return a != null && !a.IsReadOnly;
            });
            if (template == null) return null;

            var copy = (ElementType)template.Duplicate(NewName);
            copy.get_Parameter(BuiltInParameter.LEADER_ARROW_WIDTH)?.Set(Deg30);
            Parameter fill = copy.get_Parameter(BuiltInParameter.ARROW_FILLED);
            if (fill != null && !fill.IsReadOnly) fill.Set(1);
            return copy.Id;
        }

        /// <summary>Sets the leader arrowhead on the given tag TYPES. Returns how many were changed.</summary>
        public static int ApplyTo(Document doc, IEnumerable<ElementId> tagTypeIds, ElementId arrowId)
        {
            int n = 0;
            foreach (ElementId id in tagTypeIds.Distinct())
            {
                Parameter p = doc.GetElement(id)?.get_Parameter(BuiltInParameter.LEADER_ARROWHEAD);
                if (p == null || p.IsReadOnly) continue;
                if (p.AsElementId() != arrowId) { p.Set(arrowId); n++; }
            }
            return n;
        }
    }
}
