using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using Dalamud.Plugin.Services;

namespace BarGame.Services;

/// <summary>
/// Executes native game chatbox commands.
/// 
/// IChatGui.Print only writes local plugin/system messages.
/// It does not send public chat messages.
/// 
/// For commands like /s, /sh, /emote, etc., we use the game's
/// RaptureShellModule command path through FFXIVClientStructs.
/// </summary>
public sealed class GameChatService
{
    private readonly IPluginLog log;
    private readonly Queue<QueuedChatCommand> pendingCommands = new();

    private DateTime nextCommandAllowedAt = DateTime.MinValue;

    public GameChatService(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>
    /// Queues a /s message.
    /// </summary>
    public void QueueSay(string message)
    {
        this.QueueChatMessage("/s", message);
    }

    /// <summary>
    /// Queues several /sh messages with a delay between each one.
    /// This is safer than trying to inject macro-style &lt;wait.2&gt; text.
    /// </summary>
    public void QueueShoutSequence(IEnumerable<string> messages, TimeSpan delayBetweenMessages)
    {
        foreach (var message in messages)
            this.QueueChatMessage("/sh", message, delayBetweenMessages);
    }

    private void QueueChatMessage(string channelCommand, string message)
    {
        this.QueueChatMessage(channelCommand, message, TimeSpan.Zero);
    }

    private void QueueChatMessage(string channelCommand, string message, TimeSpan delayAfterCommand)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var cleanMessage = CleanSingleLine(message);

        if (string.IsNullOrWhiteSpace(cleanMessage))
            return;

        this.QueueCommand($"{channelCommand} {cleanMessage}", delayAfterCommand);
    }

    private void QueueCommand(string command, TimeSpan delayAfterCommand)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        var cleanCommand = CleanSingleLine(command);

        if (!cleanCommand.StartsWith('/'))
            return;

        this.pendingCommands.Enqueue(new QueuedChatCommand(cleanCommand, delayAfterCommand));
    }

    /// <summary>
    /// Executes one queued command when allowed.
    /// Call this from IFramework.Update.
    /// </summary>
    public void FlushOnePendingCommand()
    {
        if (this.pendingCommands.Count == 0)
            return;

        if (DateTime.Now < this.nextCommandAllowedAt)
            return;

        var queuedCommand = this.pendingCommands.Dequeue();
        var sent = this.TryExecuteCommandNow(queuedCommand.Command);

        this.nextCommandAllowedAt = DateTime.Now.Add(
            sent ? queuedCommand.DelayAfterCommand : TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Executes a native game command immediately.
    /// 
    /// This method is unsafe because it calls game client structures directly.
    /// Keep calls funneled through the queue where possible.
    /// </summary>
    private unsafe bool TryExecuteCommandNow(string command)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            if (!command.StartsWith('/'))
            {
                return false;
            }

            var shellModule = RaptureShellModule.Instance();
            if (shellModule is null)
            {
                return false;
            }

            var uiModule = UIModule.Instance();
            if (uiModule is null)
            {
                return false;
            }

            using var commandString = new Utf8String(command);

            if (commandString.Length <= 0)
            {
                return false;
            }

            if (commandString.Length > 500)
            {
                return false;
            }

            shellModule->ExecuteCommandInner(&commandString, uiModule);
            return true;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to execute native chatbox command: {Command}", command);
            return false;
        }
    }

    private static string CleanSingleLine(string message)
    {
        return message
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private sealed record QueuedChatCommand(string Command, TimeSpan DelayAfterCommand);
}
