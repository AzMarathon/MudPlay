using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Conditions;

// Sends a game-data message's Response command when its CasterMessage appears on
// the wire. A Messages record can carry a Response — the command to fire when the
// message is seen (e.g. "desert damage" → "use water": drink from the waterskin
// as the heat warning lands). ConditionTracker only keys off AppliedMessage, so a
// message with a Response but no AppliedMessage had nothing sending it; this
// closes that gap. Matches the CasterMessage exactly (trimmed), like
// ConditionTracker's applied-message index. Rebuilds on active-set change.
public sealed class MessageResponder : IDisposable
{
    private const string LogCategory = "MessageResponder";

    private readonly MessageStore _messages;
    private readonly LogService? _log;
    // Trimmed CasterMessage → Response command (only records that carry both).
    private readonly Dictionary<string, string> _byCaster = new(StringComparer.Ordinal);

    private LineExtractor? _lines;
    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public MessageResponder(MessageStore messages, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
        _log = log;
        RebuildIndex();
        _messages.Messages.CollectionChanged += OnMessagesChanged;
    }

    // Main-window VM supplies the EngineSendGate-wrapped SendUserInput. Without it
    // the responder observes but can't send.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildIndex();

    private void RebuildIndex()
    {
        _byCaster.Clear();
        foreach (MessageRecord r in _messages.Messages)
        {
            string caster = r.CasterMessage?.Trim() ?? string.Empty;
            string response = r.Response?.Trim() ?? string.Empty;
            if (caster.Length == 0 || response.Length == 0) continue;
            _byCaster[caster] = response;   // last-wins on a duplicate caster line
        }
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine || _wireSender is null || _byCaster.Count == 0) return;
        string text = line.Text.Trim();
        if (text.Length == 0) return;
        if (_byCaster.TryGetValue(text, out string? response))
        {
            _log?.Info(LogCategory, $"message '{text}' → sending response '{response}'");
            _wireSender(Encoding.Latin1.GetBytes(response + "\r"));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _messages.Messages.CollectionChanged -= OnMessagesChanged;
    }
}
