using System;
using System.Globalization;
using Dalamud.Plugin.Ipc;
using ECommons.Automation;
using ECommons.DalamudServices;
using Newtonsoft.Json.Linq;

namespace MountHeels;

/// <summary>
/// The two ways into Simple Heels that are of any use here.
///
/// Reading is IPC: GetLocalPlayer hands back the same block Mare shares, whose DefaultOffset is
/// the height Simple Heels has already worked out for this character standing on the ground -
/// from the mod's own heels_offset attribute, or from a manual entry for that shoe. Either way
/// it is the number the mods are giving, and it follows a change of shoes on its own.
///
/// Writing is the /heels command, because a mounted character cannot be moved any other way.
/// Riders are pinned to the mount's skeleton every frame, so the draw offset that Simple Heels
/// applies on the ground is overwritten before it is drawn; it re-applies the offset afterwards
/// in its own hook, and that hook reads only the temp offset. Nothing in the IPC surface reaches
/// it - RegisterPlayer fills a different table entirely - so the command it exposes for exactly
/// this job is the way in.
/// </summary>
internal sealed class SimpleHeelsLink
{
    private readonly ICallGateSubscriber<(int Major, int Minor)> apiVersion;
    private readonly ICallGateSubscriber<string> getLocalPlayer;

    public SimpleHeelsLink()
    {
        apiVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
        getLocalPlayer = Svc.PluginInterface.GetIpcSubscriber<string>("SimpleHeels.GetLocalPlayer");
    }

    public bool Available
    {
        get
        {
            try
            {
                return apiVersion.InvokeFunc().Major >= 2;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// How far Simple Heels lifts this character while standing. Zero is a real answer: it means
    /// the shoes declare no heel height and nothing has been set by hand for them.
    /// </summary>
    public float StandingHeight()
    {
        try
        {
            var json = getLocalPlayer.InvokeFunc();
            if (string.IsNullOrWhiteSpace(json))
                return 0f;

            // Absent rather than zero is the normal case - Simple Heels leaves the field out
            // when there is nothing to report.
            return JObject.Parse(json)["DefaultOffset"]?.ToObject<float>() ?? 0f;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[MountHeels] Could not read the standing height: {ex.Message}");
            return 0f;
        }
    }

    /// <summary>
    /// Sets the offset for the pose we are in, which while riding is the one Simple Heels calls
    /// "Mounted". Every axis is named every time: the command leaves anything it is not told
    /// about at its previous value, which would otherwise leave one mount's turn on the next.
    /// </summary>
    public bool Set(float x, float y, float z, float rotationDegrees) =>
        Command($"/heels temp set height {F(y)} left {F(x)} forward {F(z)} rotate {F(rotationDegrees)} silent");

    public bool Reset() => Command("/heels temp reset");

    // Invariant culture: a comma for a decimal point would be rejected as not a number.
    private static string F(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static bool Command(string command)
    {
        try
        {
            Chat.ExecuteCommand(command);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[MountHeels] '{command}' failed: {ex.Message}");
            return false;
        }
    }
}
