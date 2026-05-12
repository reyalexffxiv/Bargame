using System;

namespace Urban888.Models;

/// <summary>
/// One raffle sale entry.
///
/// This is intentionally stored as a row, not merged by player name,
/// because venue operators may want to see repeated purchases separately.
/// </summary>
[Serializable]
public sealed class RaffleSaleRecord
{
    public string PlayerName { get; set; } = string.Empty;

    public int Randoms { get; set; }

    public long Gil { get; set; }

    public DateTime CreatedAtLocal { get; set; } = DateTime.Now;
}
