using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DungeonMapper.Controls;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// the image tool's side panel (pick a picture from the images folder, the
// pictures on this map) and a placed image's view (fit, keep shape, turn,
// picture size, replace, DM only, delete). joined players use the same
// views; their changes go to the DM (see MainWindow.Session.cs).
public partial class MainWindow
{
    // the first control of the image tool view or image view, to tell
    // whether the side panel still shows it
    private Control? _imagePanelMarker;
    private string? _imageViewId;

    /// the map the image views work on: the DM's open map, or the received map while joined
    private MapDocument ImageDoc => _client is not null ? Canvas.Document : _doc;

    private bool ImagePanelShowing => _imagePanelMarker is not null && ToolPanel.Target.Children.Contains(_imagePanelMarker);

    private void BuildImagePanel()
    {
        _imageViewId = null;
        ToolPanel.Clear("image");
        ToolPanel.AddHint(_client is not null
            ? "choose a picture from your images folder, then click a cell to place it at its own size or drag a box. it goes on the DM's map for everyone. click an image to change it."
            : "choose a picture from the images folder, then click a cell to place it at its own size or drag a box. click an image to change it, drag its handles to resize. shift+drag places over an image.");
        _imagePanelMarker = ToolPanel.Target.Children.FirstOrDefault();

        if (!ImageStore.FolderOk)
        {
            ToolPanel.AddButtonRaw(new TextBlock
            {
                Text = ImageStore.Folder.Length == 0 ? "no images folder set. pick it in settings (your shared drive)." : $"images folder not found: {ImageStore.Folder}",
                Classes = { "refusal" }, TextWrapping = TextWrapping.Wrap,
            });
            ToolPanel.AddButton("settings…", () => { CloseModals(); ShowSettings(); });
            return;
        }

        var choose = Btn(Canvas.ArmedImage is null ? "choose picture…" : "choose another picture…", primary: true);
        choose.Margin = new Thickness(0, 0, 0, 6);
        choose.Click += async (_, _) =>
        {
            ToolPanel.ButtonHook?.Invoke();
            if (await PickPicture(choose) is { } picked) ArmImage(picked);
        };
        ToolPanel.AddButtonRaw(choose);

        if (Canvas.ArmedImage is { } armed)
        {
            ToolPanel.AddButtonRaw(FieldLabel("placing"));
            ToolPanel.AddButtonRaw(ImageRow(armed, $"{armed.PixelW}x{armed.PixelH} px · a click places {armed.W * 5}x{armed.H * 5} ft", true, null));
        }

        // the pictures already on this map, a click arms one
        var used = ImageDoc.Images.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (used.Count > 0)
        {
            ToolPanel.AddButtonRaw(FieldLabel("on this map"));
            var list = new StackPanel { Spacing = 1 };
            foreach (var g in used.OrderBy(g => g.First().FileName, StringComparer.OrdinalIgnoreCase))
            {
                var first = g.First();
                bool on = Canvas.ArmedImage is { } a && string.Equals(a.Path, first.Path, StringComparison.OrdinalIgnoreCase);
                list.Children.Add(ImageRow(first, $"×{g.Count()}", on, () =>
                {
                    var tpl = first.Copy(newId: true);
                    (tpl.W, tpl.H) = MapImage.NaturalCells(tpl.PixelW, tpl.PixelH);
                    ArmImage(tpl);
                }));
            }
            ToolPanel.AddButtonRaw(new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        }
        _toolInfo = _client is null ? ToolPanel.AddInfo("") : null;
        ToolPanel.AddButton("reload pictures", () =>
        {
            Canvas.ReloadImages();
            BuildImagePanel();
            SetStatus("pictures reloaded from the images folder");
            FocusCanvas();
        });
    }

    /// the image tool view, when it is the one showing
    private void RefreshImagePanel()
    {
        if (Canvas.Mode != ToolMode.Image || !ImagePanelShowing) return;
        if (_imageViewId is { } id)
        {
            if (ImageDoc.FindImage(id) is { } im) ShowImageConfig(im); else BuildImagePanel();
        }
        else BuildImagePanel();
    }

    // a picture: thumbnail, file name, a detail line. lit when selected
    private Control ImageRow(MapImage im, string detail, bool selected, Action? onClick)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*"), ColumnSpacing = 8 };
        g.Children.Add(new Image { Source = Canvas.ImageSwatch(im, 36, 36), Width = 36, Height = 36 });
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = im.FileName, Classes = { "body" }, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = detail, Classes = { "hint" }, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        var row = new Border { Child = g, Classes = { "row" } };
        row.Classes.Set("selected", selected);
        ToolTip.SetTip(row, im.Path);
        if (onClick is not null) row.PointerPressed += (_, _) => { ToolPanel.ButtonHook?.Invoke(); onClick(); };
        return row;
    }

    private void ArmImage(MapImage tpl)
    {
        Canvas.ArmedImage = tpl;
        if (Canvas.Mode != ToolMode.Image) SetTool(ToolMode.Image);
        Canvas.ArmedImage = tpl; // SetTool clears it
        BuildImagePanel();
        SetStatus($"placing {tpl.FileName}: click a cell for {tpl.W * 5}x{tpl.H * 5} ft, or drag a box");
        FocusCanvas();
    }

    /// Asks for a picture and returns it as an image to place (reference,
    /// hash, pixel size, click size). A file outside the images folder is
    /// copied in after a yes. null when cancelled or unreadable.
    private async Task<MapImage?> PickPicture(Control anchor)
    {
        if (!ImageStore.FolderOk) { SetStatus("set the images folder in settings first", StatusKind.Warning); return null; }
        var start = await StorageProvider.TryGetFolderFromPathAsync(ImageStore.Folder);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "choose a picture", AllowMultiple = false, SuggestedStartLocation = start,
            FileTypeFilter = new[] { new FilePickerFileType("Pictures") { Patterns = ImageStore.Patterns } },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return null;
        try
        {
            if (ImageStore.Relative(path) is null)
            {
                var tcs = new TaskCompletionSource<bool>();
                var flyout = ConfirmFlyout(anchor,
                    $"{System.IO.Path.GetFileName(path)} is not in the images folder, so other computers can not find it. copy it into the images folder?",
                    () => tcs.TrySetResult(true), "copy it");
                // yes hides the flyout before it runs, so a close answers no a moment later
                flyout.Closed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => tcs.TrySetResult(false));
                if (!await tcs.Task) { SetStatus("picture not used: it is outside the images folder", StatusKind.Error); return null; }
                path = await Task.Run(() => ImageStore.CopyIn(path));
                SetStatus($"copied {System.IO.Path.GetFileName(path)} into the images folder");
            }
            return await Task.Run(() => ImageStore.FromFile(path));
        }
        catch (Exception ex)
        {
            SetStatus($"could not use that picture: {ex.Message}", StatusKind.Error);
            return null;
        }
    }

    // ---------- a placed image ----------

    private void ShowImageConfig(MapImage im)
    {
        string id = im.Id;
        var doc = ImageDoc;
        bool joined = _client is not null;
        Canvas.SelectedImageId = id;
        Canvas.Refresh();
        ToolPanel.Clear("image");
        _imagePanelMarker = null;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("56,*"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new Image { Source = Canvas.ImageSwatch(im, 56, 56), Width = 56, Height = 56 });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        titles.Children.Add(new TextBlock { Text = im.FileName, Classes = { "strong" }, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        var (_, why) = Canvas.ImageState(im);
        titles.Children.Add(Tx($"{im.W * 5}x{im.H * 5} ft" + (im.PixelW > 0 ? $" · {im.PixelW}x{im.PixelH} px" : ""), "muted"));
        if (why is not null) titles.Children.Add(new TextBlock { Text = why, Classes = { "refusal" }, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        ToolTip.SetTip(head, im.Path);
        ToolPanel.AddButtonRaw(head);
        _imagePanelMarker = head;
        _imageViewId = id;

        void Changed(string? status = null)
        {
            CommitImage(im);
            Canvas.Refresh();
            if (status is not null) SetStatus(status);
            if (ImageDoc.Images.Contains(im)) ShowImageConfig(im); else BuildImagePanel();
            FocusCanvas();
        }

        ToolPanel.AddChips(new[] { "fit", "fill", "stretch" }, im.Fit.ToString().ToLowerInvariant(), v =>
        {
            var f = Enum.Parse<ImageFit>(v, ignoreCase: true);
            if (f == im.Fit) return;
            im.Fit = f;
            Changed();
        }, "how the picture fills its box");
        ToolPanel.AddHint("fit shows all of it, fill crops it to cover the box, stretch fills the box exactly.");

        ToolPanel.AddCheck("keep shape while resizing", im.KeepShape, v =>
        {
            if (v == im.KeepShape) return;
            im.KeepShape = v;
            // turning it on brings the box to the picture's shape now
            if (v && im.PixelW > 0) (im.W, im.H) = MapImage.Shaped(im.W, im.H, im.Aspect, widthLeads: true);
            Changed();
        }, "the handles keep the box close to the picture's proportions");

        AddActions(
            ("turn left", false, () => { im.Turns -= 1; (im.W, im.H) = (im.H, im.W); Changed("turned left"); }),
            ("turn right", false, () => { im.Turns += 1; (im.W, im.H) = (im.H, im.W); Changed("turned right"); }),
            ("picture size", false, () =>
            {
                if (im.PixelW <= 0) { SetStatus("the picture's size is not known", StatusKind.Warning); return; }
                (im.W, im.H) = MapImage.NaturalCells(im.PixelW, im.PixelH, im.Turns);
                Changed($"back to the picture's size ({im.W * 5}x{im.H * 5} ft)");
            }));

        if (!joined) AddDmOnlyCheck(() => im.DmOnly, v => im.DmOnly = v);
        if (im.CreatedBy is { } by) ToolPanel.AddInfo($"placed by {by}");

        var replace = Btn("replace picture…");
        replace.Margin = new Thickness(0, 6, 6, 6);
        replace.Click += async (_, _) =>
        {
            ToolPanel.ButtonHook?.Invoke();
            if (await PickPicture(replace) is not { } p || !ImageDoc.Images.Contains(im)) return;
            im.Path = p.Path; im.Hash = p.Hash; im.PixelW = p.PixelW; im.PixelH = p.PixelH;
            if (im.KeepShape) (im.W, im.H) = MapImage.Shaped(im.W, im.H, im.Aspect, widthLeads: true);
            Changed($"picture replaced with {im.FileName}");
        };
        var reuse = Btn("place more");
        reuse.Margin = new Thickness(0, 6, 6, 6);
        reuse.Click += (_, _) =>
        {
            ToolPanel.ButtonHook?.Invoke();
            var tpl = im.Copy(newId: true);
            (tpl.W, tpl.H) = MapImage.NaturalCells(tpl.PixelW, tpl.PixelH);
            ArmImage(tpl);
        };
        var row = new WrapPanel();
        row.Children.Add(replace);
        row.Children.Add(reuse);
        ToolPanel.AddButtonRaw(row);

        AddActions(
            ("back", false, () => { Canvas.SelectedImageId = null; BuildImagePanel(); Canvas.Refresh(); FocusCanvas(); }),
            ("delete", true, () =>
            {
                if (!ImageDoc.Images.Remove(im)) return;
                Canvas.SelectedImageId = null;
                if (joined)
                {
                    SendImageDeleteToHost(im.Id);
                    Canvas.Refresh();
                    BuildImagePanel();
                    SetStatus("image deleted");
                }
                else
                {
                    AfterDelete("image");
                }
            }));

        if (!joined)
            SetReopen(() =>
            {
                if (_doc.FindImage(id) is { } again) ShowImageConfig(again);
                else { Canvas.SelectedImageId = null; BackToToolView(); }
            });
    }

    /// after an image edit: an undo step for the DM, sent to the DM for a player
    private void CommitImage(MapImage im)
    {
        if (_client is not null) SendImageToHost(im);
        else ActiveHistory.Commit(_doc);
        RefreshMaps();
    }
}
