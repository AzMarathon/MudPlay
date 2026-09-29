using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.Import;

namespace MudPlay.ViewModels.GameData.Edit;

// One message in the compare-with-seed dialog: the seed record beside the user's, field
// by field, plus the user's pick. Defaults to keeping theirs so Apply changes nothing
// the user didn't choose.
public sealed partial class MessageSeedCompareEntry : ObservableObject
{
    public SeedDelta<MessageRecord>.Difference Difference { get; }

    public string Name { get; }

    // What kind of difference this is, for the list and the detail header.
    public string KindLabel { get; }

    // The two choices, worded for the kind (e.g. a removed seed record is "restored").
    public string KeepLabel { get; }
    public string UseSeedLabel { get; }

    // Field / seed value / the user's value, in record order; Changed marks the fields
    // that differ. A side with no record at all leaves its values null.
    public IReadOnlyList<FieldDiff> Fields { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepMine))]
    private bool _useSeed;

    // The inverse of UseSeed, so each of the entry's two radio buttons binds its own flag.
    public bool KeepMine
    {
        get => !UseSeed;
        set => UseSeed = !value;
    }

    public MessageSeedCompareEntry(SeedDelta<MessageRecord>.Difference difference)
    {
        ArgumentNullException.ThrowIfNull(difference);
        Difference = difference;
        MessageRecord shown = difference.Current ?? difference.Seed!;
        Name = string.IsNullOrWhiteSpace(shown.Name) ? "(unnamed)" : shown.Name;
        (KindLabel, KeepLabel, UseSeedLabel) = difference.Kind switch
        {
            SeedDifferenceKind.Edited   => ("Text edited — your copy replaces the seed's", "Keep mine", "Use seed"),
            SeedDifferenceKind.Override => ("Flags, links, fumble line or cast response edited", "Keep mine", "Use seed"),
            SeedDifferenceKind.Added    => ("Yours only — not in the seed", "Keep it", "Remove it"),
            _                           => ("Seed only — you removed it", "Keep removed", "Restore it"),
        };
        Fields = BuildFields(difference.Seed, difference.Current);
    }

    private static IReadOnlyList<FieldDiff> BuildFields(MessageRecord? seed, MessageRecord? mine)
    {
        List<FieldDiff> fields = new(10);
        Add("Name",          r => r.Name);
        Add("Caster",        r => r.CasterMessage);
        Add("Target",        r => r.TargetMessage);
        Add("Witness",       r => r.WitnessMessage);
        Add("Applied",       r => r.AppliedMessage);
        Add("Wears off",     r => r.AppliedEndsWith);
        Add("Flags",         r => $"{r.Flags} (0x{r.RawFlagsHex:X4})");
        Add("Links",         FormatLinks);
        Add("Fumble line",   r => r.ConfuseFumbleLine);
        Add("Cast response", r => r.CastResponse);
        return fields;

        void Add(string label, Func<MessageRecord, string> read)
        {
            string? seedValue = seed is null ? null : read(seed);
            string? mineValue = mine is null ? null : read(mine);
            bool changed = !string.Equals(seedValue ?? string.Empty, mineValue ?? string.Empty, StringComparison.Ordinal);
            fields.Add(new FieldDiff(label, seedValue, mineValue, changed));
        }
    }

    // In stored order: a reorder is an edit too (the seed diff compares Links unsorted).
    private static string FormatLinks(MessageRecord r)
        => r.Links is null ? string.Empty : string.Join(", ", r.Links.Select(l => $"{l.Table} #{l.Number}"));
}
