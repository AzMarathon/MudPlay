using MudPlay.Models.Profile;

namespace MudPlay.Services;

// What importing one MegaMUD character file will do, worked out before anything is
// written: the review lines (carried over, and left behind with why), and the write
// itself. The BBS user ID and password are held only here, in memory, until the
// user accepts the import; they are stored the way every MudPlay login is, encrypted
// on the character.
public sealed class MegaMudImportPlan
{
    private readonly Action<CharacterProfile, IReadOnlyDictionary<string, string>?> _apply;
    private readonly Action<Models.Settings.BbsProfile, IReadOnlyDictionary<string, string>?>? _applyBbs;
    private readonly string? _userId;
    private readonly string? _password;

    internal MegaMudImportPlan(string suggestedName, string? bbsName, string? userId, string? password,
        IReadOnlyList<MegaMudImportLine> lines, Action<CharacterProfile, IReadOnlyDictionary<string, string>?> apply,
        Action<Models.Settings.BbsProfile, IReadOnlyDictionary<string, string>?>? applyBbs = null)
    {
        _applyBbs = applyBbs;
        SuggestedName = suggestedName;
        MegaMudBbsName = bbsName;
        _userId = userId;
        _password = password;
        Lines = lines;
        _apply = apply;
    }

    // The character name to offer: the file's own name.
    public string SuggestedName { get; }

    // What MegaMUD called the board, for the review only.
    public string? MegaMudBbsName { get; }

    public bool HasLogin => _userId is not null || _password is not null;

    // The file carries redial and cleanup settings, which belong to the board.
    public bool HasBbsSettings => _applyBbs is not null;

    // Write the file's redial and cleanup settings onto the board. They are the
    // board's, so every character on it gets them; the review asks first.
    public void ApplyToBbs(Models.Settings.BbsProfile bbs, IReadOnlyDictionary<string, string>? edits = null)
    {
        ArgumentNullException.ThrowIfNull(bbs);
        _applyBbs?.Invoke(bbs, edits);
    }

    public IReadOnlyList<MegaMudImportLine> Lines { get; }

    // Write the imported settings onto a newly made character. withLogin also stores
    // the file's user ID and password as that character's login for bbsName. edits
    // are the values changed in the review, by MegaMudImportLine.EditKey.
    public void ApplyTo(CharacterProfile profile, string bbsName, bool withLogin, PasswordProtector passwords,
        IReadOnlyDictionary<string, string>? edits = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(passwords);
        _apply(profile, edits);
        if (!withLogin || !HasLogin || string.IsNullOrWhiteSpace(bbsName)) return;

        profile.BbsCredentials ??= new Dictionary<string, BbsCredentials>(StringComparer.OrdinalIgnoreCase);
        if (!profile.BbsCredentials.TryGetValue(bbsName, out BbsCredentials? login))
            profile.BbsCredentials[bbsName] = login = new BbsCredentials();
        if (_userId is not null) login.EncryptedUsername = passwords.Protect(_userId);
        if (_password is not null) login.EncryptedPassword = passwords.Protect(_password);
    }
}
