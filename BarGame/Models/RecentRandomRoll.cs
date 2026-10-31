namespace BarGame.Models;

/// <summary>An authentic observed /random, whether or not it spent a paid attempt.</summary>
public readonly record struct RecentRandomRoll(string PlayerName, int Value, bool Counted, bool Winner);
