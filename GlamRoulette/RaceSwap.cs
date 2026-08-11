using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>
/// Brings people to what the designs expect before they are dressed: female Hrothgar turn up as
/// Elezen, Lalafell as Miqo'te, men as women, and everybody at a full bust. Glamourer can do any
/// of that and handles all the awkward parts - the face and hair numbers do not mean the same
/// thing from one race to the next - but it only matches one named character at a time, so there
/// is no way to say "every one of them" without something watching who is in front of you. That
/// is this.
/// </summary>
internal sealed class RaceSwap(Configuration config, GlamourerIpc glamourer)
{
    /// <summary>Penumbra.GameData.Enums.Race, which is what Glamourer's state speaks.</summary>
    public const byte Elezen = 2;
    public const byte Lalafell = 3;
    public const byte Miqote = 4;
    public const byte Hrothgar = 7;

    private const byte Male = 0;
    private const byte Female = 1;

    /// <summary>
    /// The top of the game's bust slider. The slider holds a hundred values, so the top of it is
    /// ninety-nine and not a hundred - which is the same for every female tribe, checked against
    /// the game's own character-creation data rather than assumed.
    ///
    /// The difference is not one notch. Glamourer validates what it is handed against the
    /// wearer's set, and an invalid value is not clamped to the nearest valid one - it is reset
    /// to the first entry, which on a slider is zero. So a hundred did not mean "as large as it
    /// goes", it meant "as small as it goes", and everybody it was applied to came out flatter
    /// than they started.
    /// </summary>
    private const byte FullBust = 99;

    /// <summary>Object indices we have asked about, and when, so a re-ask is spaced out rather
    /// than sent every pass.</summary>
    private readonly Dictionary<int, DateTime> asked = [];

    /// <summary>Who Glamourer has already turned down, so a refusal is said once.</summary>
    private readonly HashSet<int> refused = [];

    /// <summary>When each person's look last had to be put back, newest last. A change that
    /// keeps needing to be made again is a fight with whoever is really holding their
    /// appearance - Mare, usually - and every round of it is a redraw that keeps the street
    /// loud. Three rounds inside the window and they are left alone for a good while.</summary>
    private readonly Dictionary<string, List<DateTime>> fights = [];

    /// <summary>Who has been left alone, and until when.</summary>
    private readonly Dictionary<string, DateTime> ceasefires = [];

    private static readonly TimeSpan FightWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Ceasefire = TimeSpan.FromMinutes(30);

    public int Count => asked.Count;

    public IEnumerable<int> Indices => asked.Keys;

    /// <summary>
    /// Whether this one is a man we are going to show as a woman. Asked by the wardrobe as well
    /// as here: Glamourer changes the model without rewriting the customize data underneath, so
    /// somebody already turned still reads as a man, and the female-only rule would pass them
    /// over for being what they no longer look like.
    /// </summary>
    public bool Feminising(ICharacter character)
    {
        var customize = character.Customize;
        if (customize.Length <= (int)CustomizeIndex.Gender || customize[(int)CustomizeIndex.Gender] != Male)
            return false;

        return character.ObjectKind == ObjectKind.Pc ? config.TurnMalePlayers : config.TurnMaleNpcs;
    }

    /// <summary>
    /// Returns true only when this is the first time we have asked for this one, because either
    /// change redraws the character and a redraw drops whatever they were wearing. Once they are
    /// already what we asked for the same request costs nothing: Glamourer compares it against
    /// what is drawn, finds no difference, and skips the redraw.
    /// </summary>
    public bool Handle(ICharacter character, ref int budget)
    {
        if (!glamourer.Available)
            return false;

        var turning = Feminising(character);

        // The race swaps are about women, and one we are about to make is one of them - so a
        // male Hrothgar becomes an Elezen woman in a single change rather than becoming a
        // Hrothgar woman first and being moved again on the pass after.
        //
        // Nobody is two races, so at most one of these can apply.
        var woman = turning || IsFemale(character);
        var (race, clan) = (byte?)null switch
        {
            _ when config.SwapHrothgarFemales && woman && Is(character, Hrothgar)
                => ((byte?)Elezen, (byte?)config.HrothgarFemaleClan),
            _ when config.SwapLalafell && woman && Is(character, Lalafell)
                => ((byte?)Miqote, (byte?)config.LalafellClan),
            _ => (null, null),
        };

        // Only for the women, including one we are about to make. On a man it is a slider his
        // body does not use, and asking for it would buy a redraw for nothing.
        var bust = config.MaxBust && woman;

        if (!turning && race == null && !bust)
        {
            asked.Remove(character.ObjectIndex);
            return false;
        }

        var index = character.ObjectIndex;
        var fresh = !asked.ContainsKey(index);

        var person = Wardrobe.KeyOf(character);
        if (ceasefires.TryGetValue(person, out var until))
        {
            if (DateTime.UtcNow < until)
                return false;
            ceasefires.Remove(person);
            fights.Remove(person);
        }

        // A change here rebuilds them exactly as settling their mods does, so it comes out of the
        // same budget. Left unbounded it was fine while this was the occasional female Hrothgar;
        // it is most of a street now, and a crowd arriving took every one of those redraws in the
        // same frame - which on login is a client still streaming, and a character rendered black.
        if (fresh && budget <= 0)
            return false;

        if (!fresh)
        {
            if (!config.Reapply)
                return false;

            if (DateTime.UtcNow - asked[index] < TimeSpan.FromSeconds(config.ReapplySeconds))
                return false;
        }

        // Everything they need in one call, so somebody who is a man and a Hrothgar and due a
        // bust is one redraw rather than three.
        var result = glamourer.SetLook(index, race, clan, turning ? Female : null,
            bust ? FullBust : null);

        asked[index] = DateTime.UtcNow;

        if (result is not (GlamourerIpc.Result.Success or GlamourerIpc.Result.NothingDone))
        {
            // Said once per person rather than swallowed. Somebody Glamourer will not let us
            // touch simply stays as they were, and watching them and seeing nothing happen
            // cannot tell you whether nothing was tried or everything was refused.
            if (refused.Add(index))
                Svc.Log.Warning($"[GlamRoulette] Glamourer would not change {Wardrobe.KeyOf(character)}: "
                                + $"{result} - {GlamourerIpc.Explain(result)}");

            return false;
        }

        refused.Remove(index);

        // Success means something actually changed - either the first time, or their look had
        // been put back by whoever else is holding it. Count the rounds and stop fighting.
        if (result == GlamourerIpc.Result.Success)
        {
            var bouts = fights.TryGetValue(person, out var list) ? list : fights[person] = [];
            bouts.RemoveAll(t => DateTime.UtcNow - t > FightWindow);
            bouts.Add(DateTime.UtcNow);
            if (bouts.Count >= 3)
            {
                ceasefires[person] = DateTime.UtcNow + Ceasefire;
                Svc.Log.Warning($"[GlamRoulette] {person}'s look has been put back {bouts.Count} "
                                + "times in ten minutes - somebody else is holding it (Mare, "
                                + "usually), so it is theirs for the next half hour.");
            }
        }

        if (fresh)
            budget--;

        if (fresh)
            Svc.Log.Information($"[GlamRoulette] {person} is {Became(race, turning)} now");

        return fresh;
    }

    /// <summary>Ask again for everyone, for when the clan setting changes.</summary>
    public void Forget()
    {
        asked.Clear();
        refused.Clear();
    }

    /// <summary>Drops the ones who are no longer around.</summary>
    public void Sweep(HashSet<int> present)
    {
        foreach (var index in asked.Keys.Where(i => !present.Contains(i)).ToList())
            asked.Remove(index);

        refused.RemoveWhere(i => !present.Contains(i));
    }

    /// <summary>What they became, for saying so once.</summary>
    private static string Became(byte? race, bool turning) => race switch
    {
        Elezen => turning ? "an Elezen woman" : "an Elezen",
        Miqote => turning ? "a Miqo'te woman" : "a Miqo'te",
        _ => turning ? "a woman" : "at a full bust",
    };

    /// <summary>
    /// The race in the customize data is what they really are, not what is being drawn -
    /// Glamourer changes the model without rewriting this - so someone already swapped still
    /// reads as a Hrothgar here. That is what makes them findable again after a redraw.
    /// </summary>
    private static bool Is(ICharacter character, byte race)
    {
        var customize = character.Customize;
        return customize.Length > (int)CustomizeIndex.Race
               && customize[(int)CustomizeIndex.Race] == race;
    }

    private static bool IsFemale(ICharacter character)
    {
        var customize = character.Customize;
        return customize.Length > (int)CustomizeIndex.Gender
               && customize[(int)CustomizeIndex.Gender] == Female;
    }
}
