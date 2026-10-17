namespace MudPlay.ViewModels.Guides;

// One line of a step's checklist. How it ticks:
//   - Probe set: ticked while the probe reads true (ActionKey, if also set, only
//     decides which control glows while the line is next);
//   - ActionKey only: ticked once the code reports that action (GuideActions);
//   - neither: an explanation line, never ticked — the step it sits in moves on
//     with Next.
internal sealed record GuideLine(string Text, string? ActionKey = null, Func<bool>? Probe = null)
{
    public bool IsTracked => ActionKey is not null || Probe is not null;
}

// A step of a guide: a titled checklist. TargetName rings a named control on the
// main window while the step is shown. Outcome, when set, completes the step even
// if its lines were done off-guide. Include, when set, drops the step from a run
// that starts while it reads false (the setup tour shows only what's missing).
internal sealed record GuideStep(
    string Title, IReadOnlyList<GuideLine> Lines, string? TargetName = null,
    Func<bool>? Outcome = null, Func<bool>? Include = null);

// A whole guide, as the Visual Guides picker lists it. Resumable guides reopen at
// the step the user last reached; the setup tour isn't, since its steps are built
// from whatever is still missing.
internal sealed record GuideDefinition(
    string Id, string Area, string Title, string Description,
    IReadOnlyList<GuideStep> Steps, bool Resumable = true);
