using System;
using System.Globalization;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;

namespace MountHeels;

/// <summary>
/// Simple Heels' published IPC. RegisterPlayer is the channel Mare uses to hand it another
/// player's heel data; its lookup runs before the local config and does not exclude object
/// index zero, so it works on ourselves too. That is the only way in - the emote override
/// cannot help, because Simple Heels returns its single "Mounted" identity and never gets as
/// far as consulting an override.
/// </summary>
internal sealed class SimpleHeelsIpc
{
    /// <summary>Simple Heels' stand-in emote id for "on a mount", whatever the mount is.</summary>
    public const uint MountedEmoteId = 4294836225;

    private readonly ICallGateSubscriber<(int Major, int Minor)> apiVersion;
    private readonly ICallGateSubscriber<int, string, object> registerPlayer;
    private readonly ICallGateSubscriber<int, object> unregisterPlayer;

    public SimpleHeelsIpc()
    {
        apiVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
        registerPlayer = Svc.PluginInterface.GetIpcSubscriber<int, string, object>("SimpleHeels.RegisterPlayer");
        unregisterPlayer = Svc.PluginInterface.GetIpcSubscriber<int, object>("SimpleHeels.UnregisterPlayer");
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
    /// Hands Simple Heels a config carrying nothing but the mounted offset. It only reads the
    /// mounted entry while we are on a mount, so there is no need to reproduce the rest - and
    /// good reason not to, since guessing at the rest is how you lose someone's settings.
    /// </summary>
    public bool Push(int objectIndex, float x, float y, float z, float rotation)
    {
        var json = "{" +
                   "\"Enabled\":true," +
                   "\"Version\":2," +
                   "\"DefaultOffset\":0.0," +
                   "\"HeelsConfig\":[]," +
                   "\"EmoteConfigs\":[{" +
                   $"\"Emote\":{{\"e\":{MountedEmoteId},\"c\":0}}," +
                   "\"Enabled\":true," +
                   "\"RelativeOffset\":false," +
                   $"\"Offset\":{{\"X\":{F(x)},\"Y\":{F(y)},\"Z\":{F(z)}}}," +
                   $"\"Rotation\":{F(rotation)}" +
                   "}]}";

        return Call(() => registerPlayer.InvokeAction(objectIndex, json));
    }

    /// <summary>Gives Simple Heels its own config back.</summary>
    public bool Release(int objectIndex) => Call(() => unregisterPlayer.InvokeAction(objectIndex));

    // Invariant culture: a comma for a decimal point would produce json nobody can parse.
    private static string F(float value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static bool Call(Action call)
    {
        try
        {
            call();
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[MountHeels] Simple Heels call failed: {ex.Message}");
            return false;
        }
    }
}
