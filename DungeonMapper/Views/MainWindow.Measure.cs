using System.Linq;
using System.Text;
using Avalonia.Controls;
using DungeonMapper.Controls;
using DungeonMapper.Models;

namespace DungeonMapper.Views;

// measure tool side panel: centers / corners chips, the readout, pawns caught
// by a radius grouped by affiliation, and a clear button. the canvas half is
// Controls/MapCanvas.Measure.cs.
public partial class MainWindow
{
    private TextBlock? _measureInfo;

    // wired once from the constructor
    private void InitMeasure()
    {
        Canvas.MeasureChanged += RefreshMeasureInfo;
    }

    private void BuildMeasurePanel()
    {
        ToolPanel.Clear("measure");
        ToolPanel.AddHint("drag for a distance, ctrl+click or ctrl+drag adds a leg from the end. drag out from a pawn for a radius, or shift+drag from anywhere. dragging from a pawn onto another pawn measures between them. esc clears.");
        ToolPanel.AddChips(["cell centers", "cell corners"], Canvas.MeasureCorners ? "cell corners" : "cell centers",
            v => { Canvas.MeasureCorners = v == "cell corners"; FocusCanvas(); }, "ends snap to");
        _measureInfo = ToolPanel.AddInfo("");
        ToolPanel.AddButton("clear", () => { Canvas.ClearMeasure(); FocusCanvas(); });
        RefreshMeasureInfo();
    }

    private void RefreshMeasureInfo()
    {
        if (_measureInfo is null || Canvas.Mode != ToolMode.Measure) return;
        if (!Canvas.HasMeasure) { _measureInfo.Text = "nothing measured yet"; return; }
        var sb = new StringBuilder(string.Join("\n", Canvas.MeasureReadout()));
        if (Canvas.MeasureMode == MapCanvas.MeasureKind.Radius)
        {
            // caught pawns by affiliation, then their names
            var caught = Canvas.MeasureCaughtPawns()
                .Select(p => Canvas.Library.Find(p.TemplateId))
                .Select(t => (aff: t is null ? "unknown" : PawnTemplate.NormalizeAffiliation(t.Affiliation),
                              name: t is null ? "pawn" : PawnName(t)))
                .GroupBy(x => x.aff).OrderBy(g => g.Key);
            foreach (var g in caught)
                sb.Append($"\n\n{g.Key} ({g.Count()})\n").Append(string.Join("\n", g.Select(x => "  " + x.name).OrderBy(n => n)));
        }
        _measureInfo.Text = sb.ToString();
    }
}
