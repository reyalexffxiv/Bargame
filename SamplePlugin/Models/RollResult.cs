namespace SamplePlugin.Models;

/// <summary>
/// Represents a parsed /random result from the FFXIV chat log.
/// 
/// Keeping this as a small model makes the chat parser independent from
/// the raffle business logic.
/// </summary>
public sealed record RollResult(
    string PlayerName,
    int Value
);
