using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace MountHeels;

/// <summary>
/// Watches what the player is riding and keeps Simple Heels pointed at the right offset for it.
/// Nothing is set for a mount with no offset of its own, so an unconfigured mount behaves
/// exactly as it did before this plugin existed.
/// </summary>
internal sealed class MountWatcher(Configuration config, SimpleHeelsLink heels)
{
    private const string HeightThrottleName = "MountHeelsHeight";

    /// <summary>What we last set, so it is only sent when it actually changes - this goes
    /// through a chat command, and one per frame would be absurd.</summary>
    private uint? setFor;
    private (float X, float Y, float Z, float R)? setValues;

    public uint CurrentMount { get; private set; }
    public bool Mounted { get; private set; }

    /// <summary>The height the mods are giving, re-read once a second so a change of shoes
    /// shows up without anything having to tell us about it.</summary>
    public float StandingHeight { get; private set; }

    public bool Adjusting => setFor != null;

    public void Update()
    {
        ReadState();

        // With Simple Heels gone there is nothing to hand anything back to.
        if (!heels.Available)
        {
            Forget();
            return;
        }

        // Read the height even while switched off, so the window is telling the truth about
        // what the shoes are worth rather than about the last time this was on.
        if (EzThrottler.Throttle(HeightThrottleName, 1000))
            StandingHeight = heels.StandingHeight();

        if (!config.Enabled)
        {
            Release();
            return;
        }

        // Dismounting changes the pose, and Simple Heels drops the offset itself when that
        // happens - so there is nothing to take back, only to forget.
        if (!Mounted)
        {
            Forget();
            return;
        }

        // Riding pillion has no mount of its own to look up, and the offset belongs to whoever
        // is driving anyway.
        if (CurrentMount == 0 || !config.Offsets.TryGetValue(CurrentMount, out var offset) || offset.DoesNothing)
        {
            Release();
            return;
        }

        Apply(offset);
    }

    /// <summary>The height that will actually be used for a mount, mods and trim together.</summary>
    public float HeightFor(MountOffset offset) => (offset.UseModelHeight ? StandingHeight : 0f) + offset.Y;

    /// <summary>Sets it again even if nothing has changed, for live editing.</summary>
    public void Refresh() => setValues = null;

    private void Apply(MountOffset offset)
    {
        (float X, float Y, float Z, float R) values = (offset.X, HeightFor(offset), offset.Z, offset.Rotation);

        // Shoes with no heel height and no trim add up to an offset of nothing, which is worth
        // leaving alone rather than sending.
        if (values is (0f, 0f, 0f, 0f))
        {
            Release();
            return;
        }

        if (setFor == CurrentMount && setValues == values)
            return;

        if (!heels.Set(values.X, values.Y, values.Z, values.R))
            return;

        setFor = CurrentMount;
        setValues = values;
        Svc.Log.Debug($"[MountHeels] Set {values} for mount {CurrentMount}");
    }

    /// <summary>Hands Simple Heels its own offset back, if we ever took it.</summary>
    public void Release()
    {
        if (setFor == null)
            return;

        // Only while still riding. The temp offset belongs to whatever pose you are in, so
        // resetting it after dismounting would clear one that was nothing to do with us.
        if (Mounted)
        {
            heels.Reset();
            Svc.Log.Debug("[MountHeels] Reset back to Simple Heels");
        }

        Forget();
    }

    /// <summary>Drops our claim without touching Simple Heels, for when the ride is already
    /// over as far as it is concerned.</summary>
    public void Forget()
    {
        setFor = null;
        setValues = null;
    }

    private unsafe void ReadState()
    {
        Mounted = false;
        CurrentMount = 0;

        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return;

        var character = (Character*)player.Address;
        if (character == null)
            return;

        // The same test Simple Heels makes before it calls the pose "Mounted" - which is what
        // decides whether its offset reaches a rider at all.
        Mounted = character->Mode is CharacterModes.Mounted or CharacterModes.RidingPillion;
        if (Mounted)
            CurrentMount = character->Mount.MountId;
    }
}
