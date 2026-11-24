using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using DungeonMapper.Controls;
using DungeonMapper.Models;
using DungeonMapper.Net;

namespace DungeonMapper.Views;

// joined players manage their party's (Player) pawns in the pawn management
// window. every change goes to the DM's app (template_set), which checks it
// (PlayerTemplates.Set) and sends the library back out. typing is sent after
// a short pause; what was sent and has not come back yet is kept over new
// packages so the editor is not reset under the player.
public partial class MainWindow
{
    // edits waiting for the pause to end, by template id
    private readonly Dictionary<string, PawnTemplate> _templateQueue = new();
    private DispatcherTimer? _templateTimer;
    // sent and not shown back yet: the player's own object, what was sent, when
    private readonly Dictionary<string, (PawnTemplate local, PawnTemplate sent, DateTime at)> _pendingTemplates = new();
    private static readonly TimeSpan PendingTemplateTime = TimeSpan.FromSeconds(3);
    private const int TemplateSendMillis = 400;

    /// a player changed a template: sent after a short pause (now: at once).
    /// the DM's own edits need nothing here
    private void TemplateEdited(PawnTemplate t, bool now = false)
    {
        if (!Joined || !PlayerPawns.Controlled(t)) return;
        _templateQueue[t.Id] = t;
        if (now) { FlushTemplates(); return; }
        _templateTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(TemplateSendMillis), DispatcherPriority.Background, (_, _) => FlushTemplates());
        _templateTimer.Stop();
        _templateTimer.Start();
    }

    /// sends every waiting template edit now
    private void FlushTemplates()
    {
        _templateTimer?.Stop();
        if (_templateQueue.Count == 0) return;
        var list = _templateQueue.Values.ToList();
        _templateQueue.Clear();
        if (_client is not { } cl) return;
        foreach (var t in list)
        {
            var copy = CopyTemplate(t);
            _pendingTemplates[t.Id] = (t, copy, DateTime.UtcNow);
            _ = System.Threading.Tasks.Task.Run(() => cl.SendTemplate(copy));
        }
    }

    private static PawnTemplate CopyTemplate(PawnTemplate t)
    {
        var lib = new PawnLibrary();
        lib.Pawns.Add(t);
        var c = MapSerializer.LibraryFromDtos(MapSerializer.LibraryToDtos(lib))[0];
        c.Id = t.Id;
        return c;
    }

    // what a player can change, compared to tell whether an edit came back
    private static bool SameTemplate(PawnTemplate a, PawnTemplate b) =>
        (a.Name ?? "").Trim() == (b.Name ?? "").Trim() && a.Status == b.Status && a.ColorOverride == b.ColorOverride
        && (a.OuterR, a.OuterG, a.OuterB, a.InnerR, a.InnerG, a.InnerB) == (b.OuterR, b.OuterG, b.OuterB, b.InnerR, b.InnerG, b.InnerB)
        && a.ImageB64 == b.ImageB64;

    // a new package from the DM: the player's templates in flight (or being
    // edited and unchanged by the DM) keep their objects, so the open editor
    // stays live. one the DM changed meanwhile wins, and one that is no longer
    // a Player pawn leaves the window
    private void KeepPlayerTemplates(PawnLibrary incoming)
    {
        var now = DateTime.UtcNow;
        foreach (var t in _templateQueue.Values) _pendingTemplates[t.Id] = (t, CopyTemplate(t), now);
        foreach (var (id, (local, sent, at)) in _pendingTemplates.ToList())
        {
            int i = incoming.Pawns.FindIndex(p => p.Id == id);
            bool queued = _templateQueue.ContainsKey(id);
            if (i >= 0 && SameTemplate(incoming.Pawns[i], sent))
            {
                if (!queued) _pendingTemplates.Remove(id); // it came back as sent
            }
            else if (!queued && now - at > PendingTemplateTime)
            {
                _pendingTemplates.Remove(id); // never came back: the DM's map wins
                continue;
            }
            if (i >= 0)
            {
                if (!PlayerPawns.Controlled(incoming.Pawns[i])) { _pendingTemplates.Remove(id); continue; }
                // the DM's fields (affiliation, hero) come from the DM
                local.Affiliation = incoming.Pawns[i].Affiliation;
                local.Hero = incoming.Pawns[i].Hero;
                incoming.Pawns[i] = local;
            }
            else incoming.Pawns.Add(local);
        }
        // the template open in the editor, unchanged by the DM, keeps its object
        if (_manageEditId is { } edit && ModalOverlay.IsVisible && _modalKind == "manage"
            && Canvas.Library.Find(edit) is { } open)
        {
            int i = incoming.Pawns.FindIndex(p => p.Id == edit);
            if (i >= 0 && !ReferenceEquals(incoming.Pawns[i], open) && SameTemplate(incoming.Pawns[i], open)
                && incoming.Pawns[i].Affiliation == open.Affiliation && incoming.Pawns[i].Hero == open.Hero)
                incoming.Pawns[i] = open;
        }
    }

    // after a package: the open window follows. the editor is rebuilt only
    // when its template is a new object (the DM changed it or it went)
    private void RefreshPlayerManage(PawnTemplate? editedBefore)
    {
        if (!ModalOverlay.IsVisible || _modalKind != "manage") return;
        RefreshManageList();
        var now = _manageEditId is null ? null : Canvas.Library.Find(_manageEditId);
        if (!ReferenceEquals(now, editedBefore)) RefreshManageEditor();
    }

    private void ClearPlayerTemplates()
    {
        _templateTimer?.Stop();
        _templateQueue.Clear();
        _pendingTemplates.Clear();
    }

    // the DM's side: a player added or changed a Player pawn template
    private void OnTemplateRequest(SessionHost host, TemplateRequest r)
    {
        bool isNew = ActiveLibrary.Find(r.Template.Id) is null;
        if (PlayerTemplates.Set(ActiveLibrary, ActivePresets, r.Template) is { } why) { host.Notice(r, why); return; }
        var t = ActiveLibrary.Find(r.Template.Id)!;
        StatusText.Text = isNew ? $"{r.Name} added the pawn {PawnName(t)}" : $"{r.Name} changed the pawn {PawnName(t)}";
        Canvas.InvalidateImageCache(); // template pictures are cached by id
        Canvas.Refresh();
        if (Canvas.Mode == ToolMode.Pawn) RefreshPawnPanel();
        if (_manageListHost is not null && ModalOverlay.IsVisible && _modalKind == "manage" && _manageTab == "pawns")
        {
            RefreshManageList();
            if (_manageEditId == t.Id) RefreshManageEditor();
        }
        PublishNow();
    }
}
