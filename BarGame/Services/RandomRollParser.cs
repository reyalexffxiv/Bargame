using System;
using System.Text.RegularExpressions;
using BarGame.Models;

namespace BarGame.Services;

/// <summary>
/// Parses the text of a genuine game RandomNumber chat message.
/// The caller MUST check XivChatType.RandomNumber before calling this parser.
/// No target-name fallback is used: unknown rollers cannot win a jackpot.
/// </summary>
internal static class RandomRollParser
{
    private static readonly Regex OwnRoll = new(
        @"^(?:Random!\s*)?You\s+roll\s+(?:a|an)\s+(?<roll>\d{1,3})\.$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NamedRoll = new(
        @"^(?:Random!\s*)?(?<name>[\p{L}\p{M}'-]+(?:\s+[\p{L}\p{M}'-]+)+(?:@[\p{L}\p{M}\p{N}'-]+)?)\s+rolls?\s+(?:a|an)\s+(?<roll>\d{1,3})\.$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SenderRoll = new(
        @"^(?:Random!\s*)?rolls?\s+(?:a|an)\s+(?<roll>\d{1,3})\.$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RollResult? Parse(string message, string sender, string localPlayer)
    {
        var text = Clean(message);
        var own = OwnRoll.Match(text);
        if (own.Success)
            return Create(localPlayer, own.Groups["roll"].Value);

        var named = NamedRoll.Match(text);
        if (named.Success)
            return Create(named.Groups["name"].Value, named.Groups["roll"].Value);

        var bySender = SenderRoll.Match(text);
        return bySender.Success ? Create(sender, bySender.Groups["roll"].Value) : null;
    }

    private static RollResult? Create(string player, string number)
    {
        var name = Clean(player);
        if (name.StartsWith("Random!", StringComparison.OrdinalIgnoreCase))
            name = name["Random!".Length..].Trim();

        if (string.IsNullOrWhiteSpace(name) ||
            !int.TryParse(number, out var value) || value is < 0 or > 999)
            return null;

        return new RollResult(name, value);
    }

    private static string Clean(string value) => value
        .Replace('\u00A0', ' ')
        .Replace("", " ")
        .Replace("", " ")
        .Replace("", " ")
        .Replace("", " ")
        .Replace("", " ")
        .Replace("", " ")
        .Trim();
}
