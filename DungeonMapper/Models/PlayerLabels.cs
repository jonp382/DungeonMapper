using System;
using System.Linq;

namespace DungeonMapper.Models;

/// multiplayer: labels players add, edit and delete. a player's label is
/// marked ClientEditable with their name in CreatedBy; the DM can make any
/// label client editable. any player may edit or delete any client editable
/// label the players see. the DM's app applies these to the real map.
public static class PlayerLabels
{
    public const int MaxText = 2000;
    public const int MaxSpan = 200;

    /// the label with this id, null when none
    public static MapLabel? Find(MapDocument doc, string id) =>
        id.Length == 0 ? null : doc.Labels.FirstOrDefault(l => l.Id == id);

    /// a label players may change: client editable and not hidden from them
    public static bool Editable(MapLabel? l) => l is not null && l.ClientEditable && !l.DmOnly;

    /// adds a new label or changes an editable one, from what a player sent.
    /// returns why not, null when done
    public static string? Set(MapDocument doc, MapLabel sent, string player)
    {
        if (sent.Id.Length == 0) return "that label has no id";
        var text = sent.Text.Length > MaxText ? sent.Text[..MaxText] : sent.Text;
        int w = Math.Clamp(sent.W, 1, MaxSpan), h = Math.Clamp(sent.H, 1, MaxSpan);
        // a color is all three channels or none
        var (r, g, b) = sent.HasOwnColor ? (Math.Min(sent.R, 255), Math.Min(sent.G, 255), Math.Min(sent.B, 255)) : (-1, -1, -1);
        var l = Find(doc, sent.Id);
        if (l is null)
        {
            doc.Labels.Add(new MapLabel
            {
                Id = sent.Id, Cell = sent.Cell, W = w, H = h, Align = sent.Align, Text = text,
                FontSize = sent.FontSize, R = r, G = g, B = b,
                DmOnly = false, ClientEditable = true, CreatedBy = player,
            });
            return null;
        }
        if (!Editable(l)) return "only the DM edits that label";
        l.Cell = sent.Cell; l.W = w; l.H = h; l.Align = sent.Align; l.Text = text;
        l.FontSize = sent.FontSize; l.R = r; l.G = g; l.B = b;
        return null;
    }

    /// removes an editable label. returns why not, null when done
    public static string? Delete(MapDocument doc, string id)
    {
        var l = Find(doc, id);
        if (l is null) return "that label is gone";
        if (!Editable(l)) return "only the DM deletes that label";
        doc.Labels.Remove(l);
        return null;
    }
}
