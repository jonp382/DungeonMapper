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

/// multiplayer: players add and edit Player pawn templates in the DM's
/// library (the pawn management window). they never delete one, never
/// change an affiliation or the hero mark, and only touch Player pawns.
public static class PlayerTemplates
{
    public const int MaxName = 60;

    /// a fresh id for a template a player makes (never clashes with the DM's p1, p2...)
    public static string NewId() => "pp" + Guid.NewGuid().ToString("N")[..10];

    /// adds or changes a template from what a player sent. returns why not,
    /// null when done. new templates are Player, not heroes
    public static string? Set(PawnLibrary lib, CampaignPresets presets, PawnTemplate sent)
    {
        if (string.IsNullOrEmpty(sent.Id) || sent.Id.Length > 40) return "that pawn has no id";
        var t = lib.Find(sent.Id);
        if (t is not null && !PlayerPawns.Controlled(t)) return "only the DM edits that pawn";
        string image = "";
        if (!string.IsNullOrEmpty(sent.ImageB64))
        {
            // a modified player app could send anything: a 50x50 png is kept
            // as sent (so it comes back the same), anything else is remade
            if (sent.ImageB64.Length > 4 * 1024 * 1024) return "that image is too big";
            try
            {
                var bytes = Convert.FromBase64String(sent.ImageB64);
                image = PawnImage.IsNormalized(bytes) ? sent.ImageB64 : Convert.ToBase64String(PawnImage.Compress(bytes));
            }
            catch { return "that image could not be read"; }
        }
        var name = (sent.Name ?? "").Trim();
        if (name.Length > MaxName) name = name[..MaxName];
        int C(int v) => Math.Clamp(v, 0, 255);
        if (t is null)
        {
            t = new PawnTemplate { Id = sent.Id, Affiliation = "Player", Hero = false };
            lib.Pawns.Add(t);
        }
        if (t.ImageB64 != image) { t.ImageB64 = image; t.ImageExt = image.Length > 0 ? ".png" : ""; }
        t.Name = name;
        t.ColorOverride = sent.ColorOverride;
        t.OuterR = C(sent.OuterR); t.OuterG = C(sent.OuterG); t.OuterB = C(sent.OuterB);
        t.InnerR = C(sent.InnerR); t.InnerG = C(sent.InnerG); t.InnerB = C(sent.InnerB);
        return null;
    }
}
