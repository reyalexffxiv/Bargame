using System;

namespace BarGame.Models;

/// <summary>
/// One counted /random roll in the active session, independent of sales history.
/// </summary>
[Serializable]
public sealed class RollHistoryRecord
{
    public string PlayerName { get; set; } = string.Empty;

    public int Value { get; set; }

    public DateTime CreatedAtLocal { get; set; } = DateTime.Now;
}
