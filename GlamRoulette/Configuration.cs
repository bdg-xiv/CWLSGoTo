using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;

namespace GlamRoulette;

/// <summary>A mod whose options get rolled, and the groups within it left alone.</summary>
public class ModPick
{
    public string Directory { get; set; } = string.Empty;

    /// <summary>Kept for the window, so a mod that has been uninstalled still has a name to
    /// show rather than a bare folder.</summary>
    public string Name { get; set; } = string.Empty;

    public HashSet<string> SkipGroups { get; set; } = [];

    /// <summary>
    /// Roll groups that offer the same options as one, so a colour spread over seven pieces of an
    /// outfit comes out the same colour on all seven rather than a patchwork. On, because a mod
    /// that asks the same question about each piece is asking about the outfit, and answering it
    /// seven different ways is the odd reading of it. Mods with nothing alike are unaffected.
    /// </summary>
    public bool LinkGroups { get; set; } = true;

    /// <summary>
    /// What to set this mod's priority to, or null to keep whatever it has in Penumbra. Setting
    /// options at all means saying a priority, so the choice is between carrying yours across and
    /// naming one here - and naming one is how a mod that has to win its files against another
    /// keeps doing so while we are rolling it.
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Which of a group's options are in play, by group. A group that is not in here has all of
    /// its options in play - the usual case, and what a freshly added mod starts as.
    /// </summary>
    public Dictionary<string, HashSet<string>> GroupOptions { get; set; } = [];

    /// <summary>The options a group may use, or null for all of them.</summary>
    public HashSet<string>? Allowed(string group)
        => GroupOptions.TryGetValue(group, out var allowed) ? allowed : null;
}

public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Leave party members in the glamour they chose.</summary>
    public bool SkipParty { get; set; }

    /// <summary>Take a turn yourself. Off by default: the point of this is what other people
    /// look like, and your own glamour is presumably one you picked.</summary>
    public bool IncludeMe { get; set; }

    /// <summary>Deal to retainers as well as to players.</summary>
    public bool IncludeRetainers { get; set; } = true;

    /// <summary>
    /// Deal to NPCs as well. Only ever the ones built on a playable body - a design is a list of
    /// gear for a human skeleton, and there is nothing sensible to put it on otherwise - and most
    /// of them have no class, so they draw from the whole pool rather than from a discipline's.
    /// </summary>
    public bool IncludeNpcs { get; set; } = true;

    /// <summary>
    /// How long one of your own outfits lasts before it goes back in the pack. Only yours:
    /// everybody else keeps theirs so they stay recognisable, but a random glamour of your own
    /// that never changes is just a glamour. Zero keeps them the same way everyone else's are.
    /// </summary>
    public int MyRotateMinutes { get; set; } = 30;

    /// <summary>When each of your own outfits was dealt, by assignment key, so the clock is per
    /// job rather than shared - and so it survives a relog.</summary>
    public Dictionary<string, DateTime> MyOutfitSince { get; set; } = [];

    /// <summary>
    /// Put people back to their Glamourer automation rather than to their bare gear. This is
    /// what "back to normal" means for anyone who has an automated design - reverting outright
    /// takes that off too.
    /// </summary>
    public bool RestoreAutomation { get; set; } = true;

    /// <summary>Only dress female characters, since the designs are cut for them.</summary>
    public bool FemaleOnly { get; set; } = true;

    /// <summary>Draw from a different pool depending on what the wearer is: a subfolder per
    /// discipline, under the design folder.</summary>
    public bool MatchJobCategory { get; set; } = true;

    /// <summary>Designs sitting directly in the design folder rather than in one of the
    /// discipline subfolders are fair game for everyone.</summary>
    public bool IncludeSharedDesigns { get; set; } = true;

    // Per-role, checked first.
    public string TankFolder { get; set; } = "tank";
    public string MeleeFolder { get; set; } = "melee";
    public string RangedFolder { get; set; } = "ranged";
    public string CasterFolder { get; set; } = "caster";
    public string HealerFolder { get; set; } = "healer";
    public string CrafterFolder { get; set; } = "crafter";
    public string GathererFolder { get; set; } = "gatherer";

    // The coarser bucket, used when a role has no folder of its own.
    public string WarFolder { get; set; } = "war";
    public string MagicFolder { get; set; } = "magic";

    /// <summary>Show female Hrothgar as Elezen instead.</summary>
    public bool SwapHrothgarFemales { get; set; } = true;

    /// <summary>Show Lalafell as Miqo'te instead. The same idea as the Hrothgar swap and for the
    /// same reason: the designs are cut for a tall body, and a Lalafell in one is a mesh that
    /// does not fit the wearer rather than an outfit.</summary>
    public bool SwapLalafell { get; set; } = true;

    /// <summary>Which Miqo'te clan they become. Penumbra.GameData.Enums.SubRace: 7 is Seeker of
    /// the Sun, 8 is Keeper of the Moon.</summary>
    public byte LalafellClan { get; set; } = 7;

    /// <summary>
    /// Show men as women. Separate for players and NPCs because they are separate decisions: a
    /// city is mostly men who are scenery, and the people around you are not. Whoever is turned
    /// counts as a woman for the female-only rule as well, or they would be changed and then
    /// passed over for still reading as men in the data Glamourer does not rewrite.
    /// </summary>
    public bool TurnMalePlayers { get; set; }

    public bool TurnMaleNpcs { get; set; }

    /// <summary>
    /// Put everybody's bust slider at the top before anything else touches them. That is the
    /// game's own customization rather than a bone scale, so it changes the mesh the body is
    /// built from - and Customize+ then scales whatever it finds, which makes this the floor the
    /// roll is measured from rather than a competitor to it.
    /// </summary>
    public bool MaxBust { get; set; }

    /// <summary>Which Elezen clan they become. Penumbra.GameData.Enums.SubRace: 3 is Wildwood,
    /// 4 is Duskwight.</summary>
    public byte HrothgarFemaleClan { get; set; } = 3;

    /// <summary>Re-dye each outfit - one colour per channel across the whole outfit - so two
    /// people in the same design still differ.</summary>
    public bool RandomizeDyes { get; set; } = true;

    /// <summary>Roll the second dye channel separately instead of matching the first.</summary>
    public bool DyeSecondChannel { get; set; } = true;

    /// <summary>Give everybody the bones of one Customize+ profile, with the chest rolled per
    /// person.</summary>
    public bool RandomizeShapes { get; set; }

    /// <summary>Which profile of yours the bones come from.</summary>
    public Guid ShapeProfile { get; set; } = Guid.Empty;

    /// <summary>Kept so a profile that has been deleted in Customize+ still has a name to show
    /// rather than a bare id.</summary>
    public string ShapeProfileName { get; set; } = string.Empty;

    // The two ends of the chest roll, as multiples of the vanilla size. One is untouched.
    public float ShapeMin { get; set; } = 1f;
    public float ShapeMax { get; set; } = 2f;

    /// <summary>Deal out shoes from a pool instead of the ones a design specifies.</summary>
    public bool RollShoes { get; set; }

    /// <summary>The shoes that may be dealt, by item id.</summary>
    public List<uint> ShoePool { get; set; } = [];

    /// <summary>
    /// The designs whose shoes are rolled. Named one at a time rather than "all of them": on
    /// most outfits the shoes are part of the outfit, and only the ones built around a bare leg
    /// are worth varying.
    /// </summary>
    public HashSet<Guid> RollShoesFor { get; set; } = [];

    /// <summary>How often one pair comes up against the others in the pool, by item id. Absent
    /// is one, so a pool nobody has weighted draws evenly.</summary>
    public Dictionary<uint, int> ShoeWeights { get; set; } = [];

    /// <summary>Roll the option dropdowns on the mods named below, per player.</summary>
    public bool RandomizeModOptions { get; set; }

    /// <summary>Which mods are fair game. Named one by one rather than "everything enabled":
    /// a size or body group has to match the wearer, and rolling one of those gives you gaps
    /// and clipping rather than variety.</summary>
    public List<ModPick> RandomizedMods { get; set; } = [];

    /// <summary>
    /// Mods that cannot be on at once because they replace the same files. Each player is given
    /// only the one their outfit needs, so two outfits built on the same base item can both be
    /// in the pool.
    /// </summary>
    public List<ModPick> ExclusiveMods { get; set; } = [];

    // How often each tier comes up, relative to the others. There are far more standard dyes
    // than metallic ones, so the metallic weight has to be well above parity just to break
    // even - these defaults land it a bit over half of all rolls.
    public int MetallicWeight { get; set; } = 12;
    public int PremiumWeight { get; set; } = 4;
    public int StandardWeight { get; set; } = 1;

    /// <summary>
    /// How often one particular dye comes up, by stain id. A dye in here answers for itself and
    /// its tier no longer speaks for it. Absent is the usual case and means "whatever the tier
    /// says", so this holds only the ones you have had an opinion about.
    /// </summary>
    public Dictionary<uint, int> DyeWeights { get; set; } = [];

    /// <summary>
    /// How often one particular outfit comes up, by design. Everything is one unless it is in
    /// here: two is twice as likely as a one, zero is never dealt without being deleted. Relative
    /// to the others in the same pool, since that is what is actually being drawn from.
    /// </summary>
    public Dictionary<Guid, int> DesignWeights { get; set; } = [];

    /// <summary>Only draw from designs whose Glamourer folder path starts with this.
    /// Empty means every design, which is rarely what anyone wants.</summary>
    public string DesignFolder { get; set; } = string.Empty;

    /// <summary>
    /// Who is wearing what, keyed by "Name@World". This is the whole point of the plugin
    /// being stable rather than a slot machine, so it lives in the config rather than in
    /// memory - the same person looks the same tomorrow.
    /// </summary>
    public Dictionary<string, Guid> Assignments { get; set; } = [];

    /// <summary>When each player was last in front of us, keyed by name and world without the
    /// role, so being seen on any job keeps all of their outfits alive.</summary>
    public Dictionary<string, DateTime> LastSeen { get; set; } = [];

    /// <summary>
    /// Outfits kept no matter how long their wearer has been gone, keyed the same way as an
    /// assignment - so one role's outfit can be kept while the same player's others age out.
    /// An entry without a role is honoured too, from when pinning covered a whole player.
    /// </summary>
    public HashSet<string> Pinned { get; set; } = [];

    /// <summary>How long an unseen player keeps their outfit. Zero means forever.</summary>
    public int RememberMinutes { get; set; } = 30;

    /// <summary>Re-apply periodically, since anything that redraws a character drops the
    /// design and Glamourer will not put it back on its own.</summary>
    public bool Reapply { get; set; } = true;

    public int ReapplySeconds { get; set; } = 30;

    /// <summary>
    /// How many people may be redrawn in one pass. Changing someone's mods costs a redraw, and
    /// a crowd arriving together would otherwise take all of theirs in the same frame, which is
    /// what a freeze is. The rest wait for the next pass a second later.
    /// </summary>
    public int RedrawsPerPass { get; set; } = 1;

    /// <summary>
    /// Redraw someone as soon as their mod settings change, so the change shows at once.
    /// Turning this off leaves the settings in place to be picked up whenever the game next
    /// redraws them anyway - a zone change, a gearset, coming back into view. Slower to appear,
    /// but nothing is ever made to flicker on your account.
    /// </summary>
    public bool ForceRedraw { get; set; } = true;

    /// <summary>Redraw everyone who needs it in the same pass rather than one a second. Faster
    /// to settle, at the price of taking every redraw in one frame.</summary>
    public bool RedrawAllAtOnce { get; set; }

    /// <summary>
    /// Let other people keep whatever options they were last rebuilt with instead of being put
    /// back to their own. Penumbra's temporary settings belong to a collection rather than to a
    /// person, so a mod worn by a dozen people at once has them taking turns to redraw each other
    /// back to their own roll - and what any of them was rebuilt with was a set somebody was
    /// meant to be seen in anyway. Never applies to you.
    /// </summary>
    public bool AllowDrift { get; set; } = true;

    /// <summary>
    /// How many times each outfit has been re-rolled. The colours are worked out from who is
    /// wearing what rather than drawn, which is what keeps them from shimmering - but it also
    /// means the same design always comes back the same colour. This goes into that sum, so a
    /// re-roll is a fresh set of colours even when the same design comes up again.
    /// </summary>
    public Dictionary<string, int> Rolls { get; set; } = [];

    /// <summary>
    /// Takes the serialiser's own leavings back out. Newtonsoft writes a "$type" key alongside a
    /// dictionary's real contents when it is told to record types, and reading it back turns that
    /// into an option group by that name. Harmless - no mod has a group called $type, so nothing
    /// ever matched it - but it shows up in the window's counts and in anything printed, and a
    /// list of your choices should only have your choices in it.
    /// </summary>
    public void Tidy()
    {
        var removed = 0;
        foreach (var mod in RandomizedMods.Concat(ExclusiveMods))
        {
            if (mod.GroupOptions.Remove("$type"))
                removed++;

            removed += mod.SkipGroups.Remove("$type") ? 1 : 0;
        }

        if (removed > 0)
            Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
