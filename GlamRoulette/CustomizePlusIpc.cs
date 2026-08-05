using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>
/// Customize+'s published IPC, subscribed by label.
///
/// The call that matters is SetTemporaryProfileOnCharacter, and unlike its Penumbra namesake it
/// really is per person: the profile is filed against a permanent actor identifier - name and
/// world - at the highest priority there is. So it survives a zone change, a teleport, walking
/// away and coming back, and it needs no redraw at all, because bones are applied to the armature
/// every frame rather than baked into a model. None of the trouble the mod options give exists
/// here.
///
/// It is temporary in the sense of never being written to disk: it is gone when Customize+ or the
/// game restarts, and it is nowhere near the profiles you keep.
/// </summary>
internal sealed class CustomizePlusIpc
{
    private readonly ICallGateSubscriber<(int Breaking, int Feature)> apiVersion;
    private readonly ICallGateSubscriber<bool> isValid;

    private readonly ICallGateSubscriber<IList<(Guid UniqueId, string Name, string Path,
        List<(string Name, ushort WorldId, byte Type, ushort SubType)> Characters, int Priority, bool Enabled)>> profileList;

    private readonly ICallGateSubscriber<Guid, (int Result, string? Json)> profileById;
    private readonly ICallGateSubscriber<ushort, string, (int Result, Guid? Id)> setTemporary;
    private readonly ICallGateSubscriber<Guid, int> deleteTemporary;

    public CustomizePlusIpc()
    {
        apiVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>("CustomizePlus.General.GetApiVersion");
        isValid = Svc.PluginInterface.GetIpcSubscriber<bool>("CustomizePlus.General.IsValid");
        profileList = Svc.PluginInterface
            .GetIpcSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>(
                "CustomizePlus.Profile.GetList");
        profileById = Svc.PluginInterface
            .GetIpcSubscriber<Guid, (int, string?)>("CustomizePlus.Profile.GetByUniqueId");
        setTemporary = Svc.PluginInterface
            .GetIpcSubscriber<ushort, string, (int, Guid?)>("CustomizePlus.Profile.SetTemporaryProfileOnCharacter");
        deleteTemporary = Svc.PluginInterface
            .GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DeleteTemporaryProfileByUniqueId");
    }

    /// <summary>
    /// Customize+ is loaded, speaking a version we understand, and actually hooked. IsValid is
    /// worth asking as well as the version: it says whether its render hook took, and without
    /// that it will accept everything and show nothing.
    /// </summary>
    public bool Available
    {
        get
        {
            try
            {
                return apiVersion.InvokeFunc().Breaking >= 6 && isValid.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Every profile you have, with the folder it sits in.</summary>
    public IReadOnlyList<(Guid Id, string Name, string Path)> Profiles()
    {
        try
        {
            var list = new List<(Guid, string, string)>();
            foreach (var (id, name, path, _, _, _) in profileList.InvokeFunc())
                list.Add((id, name, path));

            return list;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read Customize+'s profile list: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// One profile's bones, as Customize+ hands them over: every template in it flattened into a
    /// single set, which is exactly the shape the temporary call takes back.
    /// </summary>
    public string? Profile(Guid id)
    {
        try
        {
            var (result, json) = profileById.InvokeFunc(id);
            if (result == 0)
                return json;

            Svc.Log.Debug($"[GlamRoulette] Customize+ would not hand over profile {id}: {result}");
            return null;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read Customize+ profile {id}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Gives one character a set of bones of their own. Returns the id it was filed
    /// under, which is how it comes off again after they have walked away.</summary>
    public Guid? Apply(int objectIndex, string profileJson)
    {
        try
        {
            var (result, id) = setTemporary.InvokeFunc((ushort)objectIndex, profileJson);
            if (result == 0)
                return id;

            Svc.Log.Debug($"[GlamRoulette] Customize+ refused object {objectIndex}: {result}");
            return null;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not shape object {objectIndex}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Takes one back off. By id rather than by character, so it works for somebody who
    /// is no longer in front of us.</summary>
    public void Release(Guid id)
    {
        try
        {
            deleteTemporary.InvokeFunc(id);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not clear Customize+ profile {id}: {ex.Message}");
        }
    }
}
