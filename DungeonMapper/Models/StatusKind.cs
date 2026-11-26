namespace DungeonMapper.Models;

/// what a status bar message is, for its color: Info plain, Error (something
/// failed or was refused) red, Warning (nothing happened, a step is missing)
/// orange, Success (saved, exported, joined, hosting) green, Player (a
/// player's change on the DM's screen) session pink
public enum StatusKind { Info, Error, Warning, Success, Player }
