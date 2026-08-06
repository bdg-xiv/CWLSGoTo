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

    private const byte Female = 1;

    /// <summary>Object indices we have asked about, and when, so a re-ask is spaced out rather
    /// than sent every pass.</summary>
    private readonly Dictionary<int, DateTime> asked = [];

    public int Count => asked.Count;

    public IEnumerable<int> Indices => asked.Keys;

    /// <summary>
    /// Returns true only when this is the first time we have asked for this one, because a
    /// change of race redraws the character and a redraw drops whatever they were wearing. Once
    /// they are already Elezen the same request costs nothing: Glamourer compares it against
    /// what is drawn, finds no difference, and skips the redraw.
    /// </summary>
    public bool Handle(ICharacter character)
    {
        if (!config.SwapHrothgarFemales || !glamourer.Available)
            return false;

        if (!IsFemaleHrothgar(character))
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

        var result = glamourer.SetClan(index, Elezen, config.HrothgarFemaleClan);
        asked[index] = DateTime.UtcNow;

        if (result is not (GlamourerIpc.Result.Success or GlamourerIpc.Result.NothingDone))
        {
            Svc.Log.Debug($"[GlamRoulette] Could not turn {Wardrobe.KeyOf(character)} into an Elezen: {result}");
            return false;
        }

        if (fresh)
            Svc.Log.Information($"[GlamRoulette] {Wardrobe.KeyOf(character)} is an Elezen now");

        return fresh;
    }

    /// <summary>Ask again for everyone, for when the clan setting changes.</summary>
    public void Forget() => asked.Clear();

    /// <summary>Drops the ones who are no longer around.</summary>
    public void Sweep(HashSet<int> present)
    {
        foreach (var index in asked.Keys.Where(i => !present.Contains(i)).ToList())
            asked.Remove(index);
    }

    /// <summary>
    /// The race in the customize data is what they really are, not what is being drawn -
    /// Glamourer changes the model without rewriting this - so someone already swapped still
    /// reads as a Hrothgar here. That is what makes them findable again after a redraw.
    /// </summary>
    private static bool IsFemaleHrothgar(ICharacter character)
    {
        var customize = character.Customize;
        return customize.Length > (int)CustomizeIndex.Gender
               && customize[(int)CustomizeIndex.Race] == Hrothgar
               && customize[(int)CustomizeIndex.Gender] == Female;
    }
}
