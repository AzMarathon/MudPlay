using System.Text.RegularExpressions;

namespace MudPlay.Game;

// Fixed replies the MajorMUD engine prints for commands and housekeeping — refusals
// ("Why would you want to rob yourself?"), door, bank, shop, gang and channel notices
// — that no spell record will ever describe and no parser reads. The unrecognized-line
// watcher skips them. Only lines that can't be taken for a spell message are listed:
// nothing with a spell's or an effect's flavour ("You feel …", "… glows …", a cast
// result) belongs here, even when the engine prints it itself.
//
// Each entry is the engine's own format string: %s / %d / %u stand for whatever it
// fills in. Wording is the Stock engine's; a realm that rewords a line simply misses.
public static partial class EngineReplyLines
{
    private static readonly string[] Formats =
    {
        "-- %s",
        "--- Message Not Sent ---",
        "--- Telepath Not Sent ---",
        "A concealed passage opens to the %s.",
        "Are you sure you want to disband %s?",
        "Banking services:",
        "Cannot find user!",
        "From %s (Yelling): \"%s\"",
        "Gang member %s has been notified of their demotion.",
        "Gang member %s has been notified of their promotion.",
        "Gang member %s is already a lieutenant in your gang.",
        "Gang member %s is not a lieutenant in your gang.",
        "Gang member %s will be notified of their demotion next time they log on.",
        "Gang member %s will be notified of their promotion next time they log on.",
        "If you are a gang leader you may lease a Gang House.",
        "New gang shop markup value set to %u percent.",
        "No more new items may be stocked in this shop.",
        "Paging is now set to %s",
        "Perhaps you should invite %s into your gang first.",
        "Please be more specific.  You could have meant any of these:",
        "Please specify a more reasonable amount.",
        "Quiet mode set",
        "Someone yells from above \"%s\"",
        "Someone yells from below \"%s\"",
        "Someone yells from the %s \"%s\"",
        "Such an action would result in a very unbalanced game.",
        "Such an attack would result in a very unbalanced combat round.",
        "Talk syntax is TALK {Fast/Slow}",
        "Thank you for repaying your loan.",
        "Thank you for your input... email sent.",
        "That %s is not open. Closing it will do nothing!",
        "That gang doesn't exist!",
        "That is not a door or a gate!",
        "The %s is locked.",
        "The %s is now closed.",
        "The %s is now open.",
        "The %s to the %s just closed.",
        "The %s to the %s just flew open.",
        "The %s to the %s just opened.",
        "The %s was already open.",
        "The %s was not locked.",
        "The bank cannot accept your deposit at this time.",
        "The door is now closed.",
        "The door is now locked.",
        "The door to the %s just closed.",
        "The door to the %s just opened.",
        "The gate is now locked.",
        "There are no exits downwards!",
        "There are no exits upwards!",
        "There are no gangs currently established!",
        "There are no stocked items to remove.",
        "There is no benefit to locking in that direction.",
        "There is no map available for this room.",
        "There is nobody to share with.",
        "There would not be enough people left in the frontrank if you did that.",
        "This gang has been disbanded.",
        "This is not a gang owned shop.",
        "This item is not currently in stock.",
        "This shop is not suitable for your training.",
        "This weapon feels heavy in your hands.",
        "To do this action, you must turn off your evil warnings.",
        "Unknown user!",
        "Verbose mode set",
        "What do you really mean?",
        "Why are you directing messages to yourself?",
        "Why are you telepathing to yourself?",
        "Why would you follow yourself?",
        "Why would you want to attack yourself?",
        "Why would you want to drag yourself around?",
        "Why would you want to follow that?",
        "Why would you want to give to that?",
        "Why would you want to look at that?",
        "Why would you want to rob from that?",
        "Why would you want to rob yourself?",
        "Why would you want to search that?",
        "You add the %s to your shops stock.",
        "You already have something lit!",
        "You are already the owner of a gang house.",
        "You are already wearing %s and it may not be removed.",
        "You are dragging %s.",
        "You are extremely quick and deadly with this weapon.",
        "You are no longer dragging %s.",
        "You are not allowed to borrow any money.",
        "You are not carrying %s.",
        "You are not currently in a gang.",
        "You are not healthy enough to enter that room.",
        "You are not wearing %s.",
        "You are now dragging %s.",
        "You are the leader - you may not leave your gang. Use DISBAND GANG",
        "You are the only one in the back rank.  You may not leave.",
        "You are the only one in the front rank.  You may not leave.",
        "You are using too much profanity - your message is not sent.",
        "You bump %s as you try to rob %s.",
        "You cannot backstab with this weapon.",
        "You do not have %s left unequipped.",
        "You do not have %s lit.",
        "You do not have a %s.",
        "You do not have any %s.",
        "You do not have enough %s.",
        "You do not have the correct item to set the markup value for this shop.",
        "You do not have the correct item to stock this shop.",
        "You do not have the correct item to unstock this shop.",
        "You don't see %s %s",
        "You don't think you are hidden.",
        "You don't think you're sneaking.",
        "You gave %s",
        "You have %d CP to distribute.",
        "You have chosen a way of life which does not allow this action.",
        "You have chosen a way of life which prevents this action.",
        "You have left %s.",
        "You have not forgotten any users.",
        "You have not progressed far enough to use the training provided here.",
        "You have now forgotten %s",
        "You have progressed too far to the evil side to do this action.",
        "You have the following %s:",
        "You have the following keys:",
        "You have to specify a person to direct to.",
        "You have to specify a person to telepath to.",
        "You just bought %s for %s",
        "You just gave %s to %s.",
        "You just joined channel %d.",
        "You just left group %d.",
        "You may not demote somebody who is not in your gang.",
        "You may not direct any messages right now.",
        "You may not drag from the colliseum",
        "You may not enter that room during a retaliation time-period.",
        "You may not enter that room while in combat.",
        "You may not enter the backrank of your own party.",
        "You may not hide that item!",
        "You may not leave your current rank in your own party.",
        "You may not save your character.  You must exit normally so that",
        "You may not sell items to a gang shop.",
        "You may not suicide or reroll during a tournament.",
        "You may not telepath while you are blocking telepaths.",
        "You may not train any further.  Please contact your sysop",
        "You may not wear an off-hand item while you have a 2-handed weapon readied.",
        "You must be a gang leader to purchase a gang house deed.",
        "You must close the door before you may lock it.",
        "You must close the gate before you may lock it.",
        "You must recharge that before you may light it again.",
        "You must rest before you may attack again.",
        "You now have %d lives remaining.",
        "You now have no weapon readied.",
        "You picked up %s %s",
        "You remove %s from the shops stock.",
        "You remove all the items from your shop and place them on the floor.",
        "You see %s add a %s to the shops stock.",
        "You see %s buy a %s.",
        "You see %s close the %s to the %s.",
        "You see %s close the door to the %s.",
        "You see %s making a deposit.",
        "You see %s making a withdrawal.",
        "You see %s open the %s to the %s.",
        "You see %s open the door to the %s.",
        "You see %s pick the lock on the %s above you.",
        "You see %s pick the lock on the %s below you.",
        "You see %s pick the lock on the %s to the %s.",
        "You see %s remove a %s from the shops stock.",
        "You see %s remove all items from the gang shop and place them on the floor.",
        "You see %s sell a %s.",
        "You stole %s %s from %s.",
        "You successfully stole %s from %s.",
        "You successfully unlocked the %s.",
        "You wish to demote yourself from leader of your gang?",
        "You withdrew %s %s.",
        "You would get %s %s for your %s.",
        "Your account has been charged %s credits as collateral.",
        "Your account has been given %s credits.",
        "Your forget list is too long.",
        "Your gang does not have enough experience for you to purchase a gang house now.",
        "Your gang leader has demoted you.",
        "Your gang leader has promoted you to the rank of lieutenant.",
        "Your skills fail as you try to rob %s.",
        "Your tracking skills fail you this time.",
    };

    private static readonly Dictionary<string, List<string[]>> ByFirstWord = BuildIndex();

    // True when the line is one of the engine's fixed replies, or a command's
    // "Syntax: …" usage line.
    public static bool Matches(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.StartsWith("Syntax: ", StringComparison.Ordinal)) return true;
        if (!ByFirstWord.TryGetValue(FirstWord(text), out List<string[]>? candidates)) return false;
        foreach (string[] literals in candidates)
            if (Fits(text, literals)) return true;
        return false;
    }

    // literals are the format's fixed pieces in order; a filled-in value sits between
    // each pair. The first piece opens the line, the last one closes it (an empty last
    // piece means the format ends on a value), and every value is at least one character.
    private static bool Fits(string text, string[] literals)
    {
        if (!text.StartsWith(literals[0], StringComparison.Ordinal)) return false;
        if (literals.Length == 1) return text.Length == literals[0].Length;
        int at = literals[0].Length;
        for (int i = 1; i < literals.Length - 1; i++)
        {
            int found = text.IndexOf(literals[i], at + 1, StringComparison.Ordinal);
            if (found < 0) return false;
            at = found + literals[i].Length;
        }
        string last = literals[^1];
        if (last.Length == 0) return text.Length > at;
        return text.Length - last.Length > at && text.EndsWith(last, StringComparison.Ordinal);
    }

    // Every listed format opens with a fixed word, so that word keys the lookup.
    private static Dictionary<string, List<string[]>> BuildIndex()
    {
        Dictionary<string, List<string[]>> index = new(StringComparer.Ordinal);
        foreach (string format in Formats)
        {
            string[] literals = PlaceholderRx().Split(format);
            string key = FirstWord(literals[0]);
            if (!index.TryGetValue(key, out List<string[]>? list)) index[key] = list = new List<string[]>();
            list.Add(literals);
        }
        return index;
    }

    private static string FirstWord(string text)
    {
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    [GeneratedRegex(@"%[sdu]", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRx();
}
