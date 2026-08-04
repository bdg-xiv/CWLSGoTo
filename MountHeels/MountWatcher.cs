using System;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace MountHeels;

/// <summary>
/// Watches what the player is riding and keeps Simple Heels pointed at the right offset for
/// it. Nothing is pushed for a mount with no offset of its own, so an unconfigured mount
/// behaves exactly as it did before this plugin existed.
/// </summary>
internal sealed class MountWatcher(Configuration config, SimpleHeelsIpc heels)
{
    private const int LocalPlayerIndex = 0;

    /// <summary>What we last handed over, so it is only pushed when it actually changes.</summary>
    private uint? pushedFor;

    public uint CurrentMount { get; private set; }
    public bool Mounted => CurrentMount != 0;
    public bool Pushing => pushedFor != null;

    public unsafe void Update()
    {
        var mount = ReadMount();
        if (mount != CurrentMount)
            CurrentMount = mount;

        if (!config.Enabled || !heels.Available)
        {
            Release();
            return;
        }

        // An unconfigured mount is deliberately not "offset zero" - it is "not ours", and
        // pushing zero would override whatever the user set up in Simple Heels itself.
        if (mount == 0 || !config.Offsets.TryGetValue(mount, out var offset) || offset.IsZero)
        {
            Release();
            return;
        }

        Apply(mount, offset);
    }

    /// <summary>Pushes again even if the mount has not changed, for live editing.</summary>
    public void Refresh()
    {
        if (pushedFor != null)
            pushedFor = null;
    }

    private void Apply(uint mount, MountOffset offset)
    {
        if (pushedFor == mount)
            return;

        if (heels.Push(LocalPlayerIndex, offset.X, offset.Y, offset.Z, offset.Rotation))
        {
            pushedFor = mount;
            Svc.Log.Debug($"[MountHeels] Pushed offset for mount {mount}");
        }
    }

    /// <summary>Hands Simple Heels its own config back, if we ever took it.</summary>
    public void Release()
    {
        if (pushedFor == null)
            return;

        heels.Release(LocalPlayerIndex);
        pushedFor = null;
        Svc.Log.Debug("[MountHeels] Released back to Simple Heels");
    }

    private static unsafe uint ReadMount()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
            return 0;

        var character = (Character*)player.Address;
        if (character == null)
            return 0;

        var mount = character->Mount;
        return mount.MountId;
    }
}
