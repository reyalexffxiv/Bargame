using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using Dalamud.Plugin.Services;

namespace Urban888.Services;

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
        this.LastStatus = "Native chat sender ready.";
    }

    /// <summary>
    /// Last visible status, shown in the Urban888 UI.
    /// </summary>
    public string LastStatus { get; private set; }

    /// <summary>
    /// Queues a /s message.
    /// </summary>
    public void QueueSay(string message)
    {
        this.QueueChatMessage("/s", message);
    }

    /// <summary>
    /// Queues a /sh message.
    /// </summary>
    public void QueueShout(string message)
    {
        this.QueueChatMessage("/sh", message);
    }

    /// <summary>
    /// Queues several /sh messages with a delay between each one.
    /// This is safer than trying to inject macro-style &lt;wait.2&gt; text.
    /// </summary>
    public void QueueShoutSequence(IEnumerable<string> messages, TimeSpan delayBetweenMessages)
    {
        foreach (var message in messages)
            this.QueueChatMessage("/sh", message, delayBetweenMessages);

        this.LastStatus = "Queued shout sequence.";
    }

    /// <summary>
    /// Queues any native chat command.
    /// </summary>
    public void QueueCommand(string command)
    {
        this.QueueCommand(command, TimeSpan.Zero);
    }

    private void QueueChatMessage(string channelCommand, string message)
    {
        this.QueueChatMessage(channelCommand, message, TimeSpan.Zero);
    }

    private void QueueChatMessage(string channelCommand, string message, TimeSpan delayAfterCommand)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            this.LastStatus = "Not queued: message was empty.";
            return;
        }

        var cleanMessage = CleanSingleLine(message);

        if (string.IsNullOrWhiteSpace(cleanMessage))
        {
            this.LastStatus = "Not queued: cleaned message was empty.";
            return;
        }

        this.QueueCommand($"{channelCommand} {cleanMessage}", delayAfterCommand);
    }

    private void QueueCommand(string command, TimeSpan delayAfterCommand)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            this.LastStatus = "Not queued: command was empty.";
            return;
        }

        var cleanCommand = CleanSingleLine(command);

        if (!cleanCommand.StartsWith('/'))
        {
            this.LastStatus = $"Not queued: command must start with '/'. Got: {cleanCommand}";
            return;
        }

        this.pendingCommands.Enqueue(new QueuedChatCommand(cleanCommand, delayAfterCommand));
        this.LastStatus = $"Queued command: {cleanCommand}";
        this.log.Information("Queued native chatbox command: {Command}", cleanCommand);
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

        if (sent)
        {
            this.LastStatus = $"Sent command: {queuedCommand.Command}";
            this.nextCommandAllowedAt = DateTime.Now.Add(queuedCommand.DelayAfterCommand);
        }
        else
        {
            this.LastStatus = $"Failed to send command: {queuedCommand.Command}";
            this.nextCommandAllowedAt = DateTime.Now.AddSeconds(1);
        }
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
                this.LastStatus = "Failed: command was empty.";
                return false;
            }

            if (!command.StartsWith('/'))
            {
                this.LastStatus = $"Failed: command must start with '/'. Got: {command}";
                return false;
            }

            var shellModule = RaptureShellModule.Instance();
            if (shellModule is null)
            {
                this.LastStatus = "Failed: RaptureShellModule.Instance() was null.";
                return false;
            }

            var uiModule = UIModule.Instance();
            if (uiModule is null)
            {
                this.LastStatus = "Failed: UIModule.Instance() was null.";
                return false;
            }

            using var commandString = new Utf8String(command);

            if (commandString.Length <= 0)
            {
                this.LastStatus = "Failed: encoded command was empty.";
                return false;
            }

            if (commandString.Length > 500)
            {
                this.LastStatus = $"Failed: command was too long. Length: {commandString.Length}.";
                return false;
            }

            shellModule->ExecuteCommandInner(&commandString, uiModule);

            this.log.Information("Executed native chatbox command: {Command}", command);
            return true;
        }
        catch (Exception ex)
        {
            this.LastStatus = $"Failed: {ex.GetType().Name}: {ex.Message}";
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
