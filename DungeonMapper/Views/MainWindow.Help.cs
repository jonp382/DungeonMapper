using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DungeonMapper.Models;
using DungeonMapper.Net;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// the Help menu (user, Nov 27): Help Article opens the help page (help/ at
// the repo root: index.html and the tool videos) in the default browser,
// About DungeonMapper shows the version and license. the help files are
// built into the exe (DungeonMapper.csproj), so a release stays one file;
// they are unpacked into a help folder next to the program when opened.
public partial class MainWindow
{
    /// the help files built into the app, by their path under help/
    public static string[] HelpFiles() =>
        typeof(MainWindow).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("help/", StringComparison.Ordinal))
            .Select(n => n["help/".Length..])
            .ToArray();

    /// unpacks the help files into dir and returns the page's path, null
    /// when the app has no help page. the page is always rewritten (small,
    /// and edits often keep its size); a video only when missing or changed
    public static string? UnpackHelp(string dir)
    {
        var asm = typeof(MainWindow).Assembly;
        string? page = null;
        foreach (var name in HelpFiles())
        {
            using var src = asm.GetManifestResourceStream("help/" + name);
            if (src is null) continue;
            var target = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var info = new FileInfo(target);
            if (!info.Exists || info.Length != src.Length || name.EndsWith(".html", StringComparison.Ordinal))
            {
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
            if (name == "index.html") page = target;
        }
        return page;
    }

    /// where the help is unpacked: a help folder next to the program, like
    /// maps/ and settings/. not the temp folder: a sandboxed browser (the
    /// Firefox snap on Linux) has its own private /tmp and can not see it
    private static string HelpDir() => Path.Combine(AppContext.BaseDirectory, "help");

    private async Task OpenHelpArticle()
    {
        string? page;
        try { page = await Task.Run(() => UnpackHelp(HelpDir())); }
        catch (Exception ex) { SetStatus($"could not open the help: {ex.Message}", StatusKind.Error); return; }
        if (page is null) { SetStatus("this build has no help article", StatusKind.Error); return; }
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        bool ok = launcher is not null && await launcher.LaunchFileInfoAsync(new FileInfo(page));
        if (ok) SetStatus("help opened in your browser", StatusKind.Info);
        else SetStatus($"could not open a browser. the help is at {page}", StatusKind.Warning);
    }

    private void ShowAbout()
    {
        // the release tag (or 1.0.0 for a local build) and the commit after "+"
        var full = NetProtocol.AppVersion;
        int plus = full.IndexOf('+');
        string version = plus < 0 ? full : full[..plus];
        string? commit = plus < 0 ? null : full[(plus + 1)..];
        if (commit is { Length: > 7 }) commit = commit[..7];

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20, 14, 20, 18) };
        body.Children.Add(new TextBlock { Text = "Dungeon Mapper", FontSize = 22, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = "A desktop dungeon mapper for old-school tabletop games. Draw rooms, walls, doors and stairs on a 5 ft grid, " +
                   "move pawns for the party and the things they meet, and share what the players may see over your network.",
            Classes = { "body" }, TextWrapping = TextWrapping.Wrap,
        });
        var facts = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 14, RowSpacing = 4 };
        void Fact(int row, string label, string value)
        {
            facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = Tx(label, "colhead"); Grid.SetRow(l, row); facts.Children.Add(l);
            var v = new TextBlock { Text = value, Classes = { "body" }, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(v, row); Grid.SetColumn(v, 1); facts.Children.Add(v);
        }
        Fact(0, "version", commit is null ? version : $"{version} ({commit})");
        Fact(1, "drawing", Program.CpuRendering ? "CPU (software)" : "graphics card");
        Fact(2, "license", "MIT License, copyright (c) 2026 jonp382");
        Fact(3, "data", "maps, backups and settings are kept in folders next to the program");
        body.Children.Add(new Border { Classes = { "infobox" }, Child = facts });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var help = Btn("Help Article", primary: true);
        help.Click += async (_, _) => { CloseModals(); await OpenHelpArticle(); FocusCanvas(); };
        var close = Btn("close");
        close.Click += (_, _) => { CloseModals(); FocusCanvas(); };
        row.Children.Add(help);
        row.Children.Add(close);
        body.Children.Add(row);

        ShowModal(460, 360, "About DungeonMapper", null, body);
        _modalKind = "about";
    }
}
