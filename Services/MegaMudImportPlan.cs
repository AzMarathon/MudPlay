using MudPlay.Models.Profile;

namespace MudPlay.Services;

// What importing one MegaMUD character file will do, worked out before anything is
// written: the review lines (carried over, and left behind with why), and the write
// itself. The BBS user ID and password are held only here, in memory, until the
// user accepts the import; they are stored the way every MudPlay login is, encrypted
// on the character.
public sealed class MegaMudImportPlan
{
    private readonly Action<CharacterProfile> _apply;
    private readonly string? _userId;
    private readonly string? _password;

    internal MegaMudImportPlan(string suggestedName, string? bbsName, string? userId, string? password,
        IReadOnlyList<MegaMudImportLine> lines, Action<CharacterProfile> apply)
    {
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

    public IReadOnlyList<MegaMudImportLine> Lines { get; }

    // Write the imported settings onto a newly made character. withLogin also stores
    // the file's user ID and password as that character's login for bbsName.
    public void ApplyTo(CharacterProfile profile, string bbsName, bool withLogin, PasswordProtector passwords)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(passwords);
        _apply(profile);
        if (!withLogin || !HasLogin || string.IsNullOrWhiteSpace(bbsName)) return;

        profile.BbsCredentials ??= new Dictionary<string, BbsCredentials>(StringComparer.OrdinalIgnoreCase);
        if (!profile.BbsCredentials.TryGetValue(bbsName, out BbsCredentials? login))
            profile.BbsCredentials[bbsName] = login = new BbsCredentials();
        if (_userId is not null) login.EncryptedUsername = passwords.Protect(_userId);
        if (_password is not null) login.EncryptedPassword = passwords.Protect(_password);
    }
}
