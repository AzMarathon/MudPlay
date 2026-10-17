namespace MudPlay.Models.Profile;

// BBS-scoped identifier for a character profile. Profiles live under
// BBS/{Bbs}/profiles/{Name}/profile.json, so the same character name on two
// different BBSes is two distinct profiles — the (Bbs, Name) pair is the only
// unambiguous key. Value equality lets callers de-dup refs directly.
public sealed record ProfileRef(string Bbs, string Name);
