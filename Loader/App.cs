using System;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Color = System.Windows.Media.Color;
using ElementSet = Autodesk.Revit.DB.ElementSet;

namespace PipeTagger
{
    /// <summary>
    /// Runs when Revit starts: adds the "Mechano" tab with "Tag Pipes & Ducts" and "Reload".
    /// The buttons call thin proxy commands that forward to the hot-loaded Core DLL.
    /// </summary>
    public class App : IExternalApplication
    {
        private const string TabName = "Mechano";
        private const string PanelName = "MEP Tags";

        public Result OnStartup(UIControlledApplication application)
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { }   // tab already exists

            RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
            string path = Assembly.GetExecutingAssembly().Location;

            panel.AddItem(new PushButtonData("PipeTagger_Tag", "Tag Pipes\n& Ducts", path, typeof(TagCommandProxy).FullName)
            {
                ToolTip = "Tag pipes or ducts by System Type, with a chosen tag for each system.",
                LargeImage = MakeIcon(32, "Ø", Color.FromRgb(0, 120, 212)),
                Image = MakeIcon(16, "Ø", Color.FromRgb(0, 120, 212))
            });

            panel.AddItem(new PushButtonData("PipeTagger_Reload", "Reload", path, typeof(ReloadCommand).FullName)
            {
                ToolTip = "Reload the latest build of the Pipe Tagger tool without restarting Revit.",
                LargeImage = MakeIcon(32, "⟳", Color.FromRgb(16, 124, 65)),
                Image = MakeIcon(16, "⟳", Color.FromRgb(16, 124, 65))
            });

            RibbonPanel sheets = application.CreateRibbonPanel(TabName, "Views & Sheets");
            sheets.AddItem(new PushButtonData("PipeTagger_ScopeViews", "Views from\nScope Boxes", path, typeof(ViewsSheetsCommandProxy).FullName)
            {
                ToolTip = "Select the main plan (or its viewport), tick scope boxes: one view per scope box " +
                          "(independent or dependent), and optionally a sheet for each, numbered first -> last.",
                LargeImage = MakeIcon(32, "▦", Color.FromRgb(202, 80, 16)),
                Image = MakeIcon(16, "▦", Color.FromRgb(202, 80, 16))
            });

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        /// <summary>Draws a button icon in code, so no image files need to ship.</summary>
        private static BitmapSource MakeIcon(int size, string glyph, Color color)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null,
                                        new Rect(0, 0, size, size), size * 0.15, size * 0.15);
                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Symbol"), size * 0.62, Brushes.White, 1.0);
                dc.DrawText(text, new System.Windows.Point((size - text.Width) / 2, (size - text.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }

    /// <summary>"Tag Pipes & Ducts" button: runs the latest Core version.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class TagCommandProxy : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            HotLoader.Run("PipeTagger.TagPipesCommand", commandData, ref message, elements);
    }

    /// <summary>"Views from Scope Boxes" button: runs the latest Core version.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ViewsSheetsCommandProxy : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            HotLoader.Run("PipeTagger.ViewsSheetsCommand", commandData, ref message, elements);
    }

    /// <summary>"Reload" button: forces a fresh load of Core and shows which build is active.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ReloadCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                Assembly core = HotLoader.GetCore(force: true);
                TaskDialog.Show("Pipe Tagger",
                    $"Reloaded ✔\n\nBuild time: {HotLoader.LoadedStamp:yyyy-MM-dd HH:mm:ss}\n" +
                    $"Loads this session: {HotLoader.LoadCount}\n\n{HotLoader.CorePath}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Pipe Tagger", "Reload failed:\n\n" + ex.Message);
                return Result.Failed;
            }
        }
    }
}
