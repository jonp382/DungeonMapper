using Avalonia.Controls;
using DungeonMapper.Controls;

namespace DungeonMapper.Views;

// fog tool side panel: the on checkbox, the vision radius slider (0 to 120 ft,
// steps of 10, shift+scroll steps it too), where the center is, and a release
// button for the anchor. the canvas half is Controls/MapCanvas.Fog.cs.
public partial class MainWindow
{
    private Slider? _fogSlider;
    private TextBlock? _fogInfo;

    // wired once from the constructor
    private void InitFog()
    {
        Canvas.FogChanged += RefreshFogInfo;
    }

    private void BuildFogPanel()
    {
        ToolPanel.Clear("fog");
        ToolPanel.AddHint("dims the map outside a vision radius. the center follows the cursor and snaps to the closest cell center, corner or pawn. click to anchor it, esc releases. shift+scroll changes the radius.");
        ToolPanel.AddCheck("fog on", Canvas.FogOn, v => { Canvas.FogOn = v; Canvas.Refresh(); FocusCanvas(); },
            "only while this tool is selected");
        _fogSlider = ToolPanel.AddSlider("vision radius", 0, MapCanvas.FogMaxFeet, MapCanvas.FogStepFeet, Canvas.FogFeet,
            v => Canvas.FogFeet = (int)v);
        _fogInfo = ToolPanel.AddInfo("");
        ToolPanel.AddButton("release anchor", () => { Canvas.ReleaseFogAnchor(); FocusCanvas(); });
        RefreshFogInfo();
    }

    private void RefreshFogInfo()
    {
        if (_fogInfo is null || Canvas.Mode != ToolMode.Fog) return;
        if (_fogSlider is not null && (int)_fogSlider.Value != Canvas.FogFeet) _fogSlider.Value = Canvas.FogFeet;
        string radius = $"radius {MapCanvas.FormatCells(Canvas.FogRadiusCells)}";
        string where = Canvas.FogAnchored ? "anchored (esc releases)" : "following the cursor";
        _fogInfo.Text = $"{radius}\n{where}";
    }
}
