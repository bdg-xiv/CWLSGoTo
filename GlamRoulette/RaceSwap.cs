using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>
/// Makes female Hrothgar turn up as Elezen instead. Glamourer can change anyone's clan and does
/// all the awkward parts - the face and hair numbers do not mean the same thing from one race to
/// the next - but it only matches one named character at a time, so there is no way to say
/// "every one of them" without something watching who is in front of you. That is this.
/// </summary>
internal sealed class RaceSwap(Configuration config, GlamourerIpc glamourer)
{
    /// <summary>Penumbra.GameData.Enums.Race, which is what Glamourer's state speaks.</summary>
    public const byte Hrothgar = 7;
    public const byte Elezen = 2;

    private const byte Male = 0;
    private const byte Female = 1;

    /// <summary>Object indices we have asked about, and when, so a re-ask is spaced out rather
    /// than sent every pass.</summary>
    private readonly Dictionary<int, DateTime> asked = [];

    /// <summary>Who Glamourer has already turned down, so a refusal is said once.</summary>
    private readonly HashSet<int> refused = [];

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
    public bool Handle(ICharacter character)
    {
        if (!glamourer.Available)
            return false;

        var turning = Feminising(character);

        // The Hrothgar swap is about women, and one we are about to make is one of them - so a
        // male Hrothgar becomes an Elezen woman in a single change rather than becoming a
        // Hrothgar woman first and being moved again on the pass after.
        var elezen = config.SwapHrothgarFemales && IsHrothgar(character) && (turning || IsFemale(character));

        if (!turning && !elezen)
        {
            asked.Remove(character.ObjectIndex);
            return false;
        }

        var index = character.ObjectIndex;
        var fresh = !asked.ContainsKey(index);

        if (!fresh)
        {
            if (!config.Reapply)
                return false;

            if (DateTime.UtcNow - asked[index] < TimeSpan.FromSeconds(config.ReapplySeconds))
                return false;
        }

        var result = glamourer.SetLook(index,
            elezen ? Elezen : null,
            elezen ? config.HrothgarFemaleClan : null,
            turning ? Female : null);

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

        if (fresh)
            Svc.Log.Information($"[GlamRoulette] {Wardrobe.KeyOf(character)} is "
                                + (elezen && turning ? "an Elezen woman now"
                                    : elezen ? "an Elezen now" : "a woman now"));

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

    /// <summary>
    /// The race in the customize data is what they really are, not what is being drawn -
    /// Glamourer changes the model without rewriting this - so someone already swapped still
    /// reads as a Hrothgar here. That is what makes them findable again after a redraw.
    /// </summary>
    private static bool IsHrothgar(ICharacter character)
    {
        var customize = character.Customize;
        return customize.Length > (int)CustomizeIndex.Race
               && customize[(int)CustomizeIndex.Race] == Hrothgar;
    }

    private static bool IsFemale(ICharacter character)
    {
        var customize = character.Customize;
        return customize.Length > (int)CustomizeIndex.Gender
               && customize[(int)CustomizeIndex.Gender] == Female;
    }
}
