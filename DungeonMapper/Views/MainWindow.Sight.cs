using Avalonia.Controls;
using DungeonMapper.Controls;
using DungeonMapper.Models;
using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// sight tool side panel: the range slider (5 to 240 ft, steps of 5, shift
// +scroll steps it too) with a number box for any value 1 to 240, where the
// viewer is, and a clear button. the canvas half is Controls/MapCanvas.Sight.cs.
public partial class MainWindow
{
    private Slider? _sightSlider;
    private TextBox? _sightBox;
    private TextBlock? _sightRefusal;
    private TextBlock? _sightInfo;
    private bool _sightSyncing;

    // wired once from the constructor
    private void InitSight()
    {
        Canvas.SightChanged += RefreshSightInfo;
    }

    private void BuildSightPanel()
    {
        ToolPanel.Clear("sight");
        ToolPanel.AddHint("click a pawn or a cell to see what it can see. green is clear, orange is behind one pawn, red behind two or more. walls, closed doors and the floor's edge block, objects are ignored. drag a pawn to move it and watch it change, hover a cell for the line to it. esc clears.");
        _sightSlider = ToolPanel.AddSlider("range", Sight.StepFeet, Sight.MaxFeet, Sight.StepFeet, Canvas.SightFeet, v =>
        {
            if (_sightSyncing) return;
            Canvas.SightFeet = (int)v;
        });
        _sightBox = ToolPanel.AddNumber("range in feet", Canvas.SightFeet, _ => { });
        _sightRefusal = RefusalLine();
        ToolPanel.AddButtonRaw(_sightRefusal);
        _sightBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { ApplySightBox(); FocusCanvas(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { RefreshSightInfo(); FocusCanvas(); e.Handled = true; }
        };
        _sightBox.LostFocus += (_, _) => ApplySightBox();
        _sightInfo = ToolPanel.AddInfo("");
        ToolPanel.AddButton("clear", () => { Canvas.ClearSight(); FocusCanvas(); });
        RefreshSightInfo();
    }

    // the box takes any whole number of feet in range; anything else is
    // refused with the red line and nothing changes
    private void ApplySightBox()
    {
        if (_sightBox is null || _sightRefusal is null) return;
        if (int.TryParse(_sightBox.Text?.Trim(), out int v) && v >= Sight.MinFeet && v <= Sight.MaxFeet)
        {
            _sightBox.Classes.Remove("invalid");
            SetRefusal(_sightRefusal, null);
            Canvas.SightFeet = v;
            RefreshSightInfo();
            return;
        }
        _sightBox.Classes.Add("invalid");
        SetRefusal(_sightRefusal, $"a whole number of feet, {Sight.MinFeet} to {Sight.MaxFeet}");
    }

    private void RefreshSightInfo()
    {
        if (_sightInfo is null || Canvas.Mode != ToolMode.Sight) return;
        _sightSyncing = true;
        if (_sightSlider is not null && (int)_sightSlider.Value != Canvas.SightFeet) _sightSlider.Value = Canvas.SightFeet;
        _sightSyncing = false;
        if (_sightBox is not null && !_sightBox.IsFocused)
        {
            _sightBox.Text = Canvas.SightFeet.ToString();
            _sightBox.Classes.Remove("invalid");
            if (_sightRefusal is not null) SetRefusal(_sightRefusal, null);
        }
        string range = $"range {Canvas.SightFeet} ft";
        _sightInfo.Text = Canvas.SightCell is CellKey c
            ? $"{range}\nfrom ({c.X}, {c.Y}){(Canvas.Document.Pawns.ContainsKey(c) ? ", a pawn" : "")}"
            : $"{range}\nclick a pawn or a cell";
    }
}
