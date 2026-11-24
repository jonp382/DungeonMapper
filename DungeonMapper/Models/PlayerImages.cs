using System;
using System.Linq;

namespace DungeonMapper.Models;

/// multiplayer: images players place, edit and delete. any player may change
/// or delete any image they can see (not DM only); there is no "players can
/// edit" flag like labels have. the DM's app applies these to the real map.
public static class PlayerImages
{
    public static MapImage? Find(MapDocument doc, string id) =>
        id.Length == 0 ? null : doc.Images.FirstOrDefault(i => i.Id == id);

    /// an image players may change
    public static bool Editable(MapImage? im) => im is not null && !im.DmOnly;

    /// adds a new image or changes an editable one, from what a player sent
    /// (already through ImageFromDto, so the path is safe and the box sane).
    /// returns why not, null when done
    public static string? Set(MapDocument doc, MapImage sent, string player)
    {
        if (sent.Id.Length == 0) return "that image has no id";
        var im = Find(doc, sent.Id);
        if (im is null)
        {
            var n = sent.Copy();
            n.DmOnly = false;
            n.CreatedBy = player;
            doc.Images.Add(n);
            return null;
        }
        if (!Editable(im)) return "only the DM edits that image";
        var by = im.CreatedBy;
        im.SetFrom(sent);
        im.DmOnly = false;
        im.CreatedBy = by;
        return null;
    }

    /// removes an editable image. returns why not, null when done
    public static string? Delete(MapDocument doc, string id)
    {
        var im = Find(doc, id);
        if (im is null) return "that image is gone";
        if (!Editable(im)) return "only the DM deletes that image";
        doc.Images.Remove(im);
        return null;
    }
}
