namespace MudPlay.Models.GameData;

// User-defined incoming-text pattern → response. Per-character; persisted
// on CharacterProfile. Loaded into TriggerEngine on profile load; the
// engine subscribes to LineExtractor, ChatRouter, and LogService —
// filtering each emitted line by Scope, matching with MatchType + Pattern,
// capturing {name} / {1} placeholders into the trigger-wildcard store
// (TriggerEngine.Variables — trigger-only, not shared with aliases/macros),
// then dispatching Response + the optional SoundFile.
//
// Scope selects which subset of incoming lines this trigger considers.
// MatchType is Literal (with * wildcards and {name} / {1} captures) or full
// regex; Pattern is in the syntax MatchType indicates. Response is sent to
// the game on match — wildcard substitution via {name} is applied first,
// multi-step via ^M or ; (same syntax as macros); a blank string is valid
// and sends a bare carriage return. SoundFile is an optional sound file
// played on match, gated and levelled by the Trigger cue on Settings → Sounds.
//
// Every trigger is the character's own, saved on CharacterProfile.Triggers.
public sealed record Trigger(
    string Name,
    bool Enabled,
    TriggerScope Scope,
    TriggerMatchType MatchType,
    string Pattern,
    string Response,
    string? SoundFile = null);

// What syntax Trigger.Pattern uses.
public enum TriggerMatchType
{
    // Substring / wildcard match. * matches any run of characters (no
    // capture). {name} (or {1}, {2}…) matches a run of characters and binds it
    // to that name in the trigger-wildcard store.
    Literal,
    // Full PCRE-flavoured regex (.NET). Named groups (?<name>…) populate
    // {name} in the trigger-wildcard store.
    Regex,
}

// Which subset of incoming lines a trigger considers.
public enum TriggerScope
{
    // Game-emitted lines that ChatRouter does not classify as chat.
    GameMessages,
    // Any chat line — catch-all for every chat channel.
    ChatAny,
    // Specific chat channel — Say (local-room talk).
    ChatSay,
    // Specific chat channel — Yell.
    ChatYell,
    // Specific chat channel — Gossip.
    ChatGossip,
    // Specific chat channel — Telepath.
    ChatTelepath,
    // Specific chat channel — Gangpath.
    ChatGangpath,
    // Specific chat channel — Broadcast.
    ChatBroadcast,
    // MudPlay's own LogService emissions.
    SystemLog,
}
