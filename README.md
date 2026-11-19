# Dungeon Mapper

A desktop dungeon mapper for old-school tabletop games. Draw rooms, walls, doors and stairs on a 5 ft grid, label them, and move pawns for your party and the things they meet. Everything runs offline on your own computer.

## Getting started

Run the `DungeonMapper` program for your system (Windows or Linux). There is nothing to install, and the app keeps its maps and settings in folders next to the program.

- **Pan:** right-click and drag
- **Zoom:** mouse wheel
- **Pick a tool:** click it in the toolbar, or press its letter

## Tools

| Tool | Key | What it does |
| --- | --- | --- |
| Select | S | Click something to edit it. Drag to select an area, then copy, cut or paste it. The clear clipboard button drops the clipboard and the paste ghost. |
| Move | M | Drag around an area, then click where it should go. Drag a single pawn to move just that pawn. |
| Room | R | Drag a rectangle to make a walled room. Drawing over an existing room merges the two. |
| Polygon | N | Click grid points to outline an odd-shaped room. Click the first point again, or press Enter, to finish. |
| Floor | F | Paint floor by dragging a rectangle, or flood fill the room you click. |
| Wall | W | Press on one grid point and release on another to draw a wall line. |
| Door | D | Click a wall to place a door. Click a door to change its type or state. |
| Stair | T | Click or drag to place stairs, then pick which way they go. |
| Label | L | Click to place a label, or drag to make one that spans an area. |
| Erase | E | Remove things, and choose what the eraser takes. |
| Pawn | P | Place pawns from your library onto the map. |

You can change the tool letters in the hotkeys menu.

## Other shortcuts

| Keys | Action |
| --- | --- |
| Ctrl+S | Save |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy, cut, paste at the cursor |
| R / Shift+R | Rotate what you are pasting or moving |
| Ctrl+F | Search pawns |
| Delete | Erase the selection |
| Esc | Cancel what you are doing, or close a menu |

## Campaigns and maps

A save file is a campaign. One campaign can hold many maps, such as one per dungeon level. Switch between them from the toolbar, and use the maps menu to add, rename, reorder, export or delete them. You can also bring a map in from another file.

Each campaign has its own:

- **Door types:** named door colors, such as locked or secret.
- **Pawn statuses:** rings drawn around a pawn, such as poisoned or asleep.
- **Affiliations:** pawn colors by side, such as party or enemy.

Any of these can be exported, and they can be imported from another campaign or map file. Importing only adds names you don't have yet. It never changes the ones you already made.

## Pawns

Pawns are tokens for characters and monsters. Each one can have a name, a picture, an affiliation color and a status. Your pawn library lets you search, sort, mark favorites and quickly reach recently used pawns. A pawn marked as a hero can only appear once in the campaign, and placing it again moves it. The library can be exported and imported too.

## Saving and safety

- **Save:** Ctrl+S saves over the file you opened. A new campaign asks where to save it the first time.
- **Autosave:** the app saves a copy every few minutes. You can set the interval in settings. Autosaves go in `maps/autosaves`.
- **Backups:** whenever you open a file, an untouched copy goes into `maps/backups` first.
- **Undo:** almost every change can be undone.

## Settings

The settings menu covers grid spacing and colors, map colors, wall thickness, the default label text and the autosave interval. Settings belong to the app, not to a campaign, and can be exported to a file and imported again on another computer.
