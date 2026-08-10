using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using Newtonsoft.Json.Linq;

namespace GlamRoulette;

/// <summary>
/// Gives everybody the bones of one Customize+ profile of yours, with the chest rolled per person
/// so they are not all the same shape. The same person comes out the same size every time - the
/// roll is derived from who they are rather than drawn - and it follows the player rather than the
/// outfit, since a body is not something you change with your job.
/// </summary>
internal sealed class Shapes(Configuration config, CustomizePlusIpc cplus)
{
    /// <summary>
    /// The chest, in the order Customize+ names them. The first pair is the vanilla bone every
    /// skeleton has; the second is IVCS's, which hangs off it and only exists on a body mod that
    /// brings it. Which of them a profile really uses is its own business, so we roll whichever
    /// ones it already touches rather than deciding for it.
    /// </summary>
    private static readonly string[] Chest = ["j_mune_l", "j_mune_r", "iv_c_mune_l", "iv_c_mune_r"];

    /// <summary>What Customize+ calls them, for saying which ones are being rolled.</summary>
    public static string NameOf(string bone) => bone switch
    {
        "j_mune_l" => "Breast Left",
        "j_mune_r" => "Breast Right",
        "iv_c_mune_l" => "Breast B Left",
        "iv_c_mune_r" => "Breast B Right",
        _ => bone,
    };

    /// <summary>The chosen profile's bones, read once and kept - it is a round trip and a JSON
    /// parse, and it only changes when you edit the profile.</summary>
    private JObject? bones;
    private Guid loaded = Guid.Empty;

    /// <summary>Which of the chest bones that profile actually scales, and by how much, so a
    /// shape somebody sculpted is kept and only its size is rolled.</summary>
    private Dictionary<string, float[]> chest = [];

    /// <summary>Who has been given what, so nothing is sent twice. A temporary profile is filed
    /// against the character rather than against an object, so it stays put through a zone change
    /// and there is nothing to put back afterwards.</summary>
    private readonly Dictionary<string, (string Signature, Guid Id, DateTime Seen, DateTime Applied)> given = [];

    /// <summary>
    /// How long a shape is taken on trust before it is simply sent again. Nothing here can see
    /// whether Customize+ still holds it - a temporary profile is replaced by whoever writes one
    /// next, and there is no telling from outside that ours has gone - so believing one apply
    /// forever meant somebody could end up their own size until something forced a re-send, and
    /// the only thing that did was moving a slider. Sending it again costs one call and no
    /// redraw, which is cheap enough to do on a clock rather than on a hunch.
    /// </summary>
    private static readonly TimeSpan Restate = TimeSpan.FromMinutes(2);

    /// <summary>Who Customize+ has already turned down, so it is said once rather than every
    /// pass for as long as they stand there.</summary>
    private readonly HashSet<string> refused = [];

    private bool wasAvailable;
    private DateTime nextPrune = DateTime.MinValue;

    public int Shaped => given.Count;

    /// <summary>The bones of the chosen profile we are rolling, for the window to name.</summary>
    public IReadOnlyCollection<string> Rolling => chest.Keys;

    /// <summary>Reads the chosen profile in, if it is not already in.</summary>
    private bool Load()
    {
        if (bones != null && loaded == config.ShapeProfile)
            return true;

        bones = null;
        chest = [];
        loaded = config.ShapeProfile;

        if (config.ShapeProfile == Guid.Empty)
            return false;

        var json = cplus.Profile(config.ShapeProfile);
        if (json == null)
            return false;

        try
        {
            bones = JObject.Parse(json)["Bones"] as JObject;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read the shape profile: {ex.Message}");
            return false;
        }

        if (bones == null)
            return false;

        foreach (var name in Chest)
        {
            if (bones[name] is not JObject bone)
                continue;

            chest[name] = Scaling(bone);
        }

        Svc.Log.Information($"[GlamRoulette] Shape profile has {bones.Count} bone(s), " +
                            (chest.Count > 0
                                ? $"rolling {string.Join(", ", chest.Keys)}"
                                : $"none of them the chest - setting {string.Join(", ", Chest)} on top"));
        return true;
    }

    private static float[] Scaling(JObject bone)
    {
        var scaling = bone["Scaling"];
        return
        [
            scaling?["X"]?.ToObject<float>() ?? 1f,
            scaling?["Y"]?.ToObject<float>() ?? 1f,
            scaling?["Z"]?.ToObject<float>() ?? 1f,
        ];
    }

    /// <summary>Gives one player their shape, if they have not already got it.</summary>
    public void Apply(int objectIndex, string playerKey, int roll)
    {
        // Availability is asked once a pass rather than once a player - it is two calls into
        // Customize+ and the answer is the same for everybody in the same frame.
        if (!config.RandomizeShapes || !wasAvailable)
            return;

        if (!Load())
            return;

        var size = Size(playerKey, roll);
        var signature = $"{config.ShapeProfile}@{size:F3}";

        // The usual case by far: they already have it, and it stays on through a zone change
        // without being sent again.
        if (given.TryGetValue(playerKey, out var already) && already.Signature == signature
            && DateTime.UtcNow - already.Applied < Restate)
        {
            given[playerKey] = already with { Seen = DateTime.UtcNow };
            return;
        }

        var profile = (JObject)bones!.DeepClone();
        Resize(profile, size);

        // Nothing to take off first - Customize+ drops whatever temporary profile that character
        // already had as it files the new one, so the old id is gone by the time this returns.
        var (id, result) = cplus.Apply(objectIndex, new JObject { ["Bones"] = profile }.ToString());
        if (id is not { } assigned)
        {
            // Once per person rather than once a second. This is retried on every pass, and a
            // refusal that repeats forever would be the only thing in the log - but a refusal
            // that never gets mentioned at all is worse, which is what it was.
            if (refused.Add(playerKey))
                Svc.Log.Warning($"[GlamRoulette] Customize+ would not shape {playerKey}: {result}");

            return;
        }

        refused.Remove(playerKey);
        given[playerKey] = (signature, assigned, DateTime.UtcNow, DateTime.UtcNow);
        Svc.Log.Debug($"[GlamRoulette] {playerKey} is {size:F2}x");
    }

    /// <summary>
    /// Puts the rolled size onto the chest bones. The profile's own proportions are kept and only
    /// scaled to the roll, so a chest that was sculpted rather than merely enlarged still has the
    /// shape you gave it - the number being rolled is its size, not its shape.
    /// </summary>
    private void Resize(JObject profile, float size)
    {
        if (chest.Count == 0)
        {
            // The profile says nothing about the chest, so the roll is all there is to say. All
            // four go in: the IVCS pair costs nothing on a skeleton that has not got them, since
            // Customize+ can only apply a bone that is there to apply it to.
            foreach (var name in Chest)
                profile[name] = new JObject
                {
                    ["Scaling"] = new JObject { ["X"] = size, ["Y"] = size, ["Z"] = size },
                };

            return;
        }

        foreach (var (name, original) in chest)
        {
            if (profile[name] is not JObject bone)
                continue;

            // Sized off the largest of the three, so "twice as big" means the biggest dimension
            // doubles rather than the volume going up eightfold.
            var basis = Math.Max(original.Max(), 0.001f);
            var factor = size / basis;

            bone["Scaling"] = new JObject
            {
                ["X"] = original[0] * factor,
                ["Y"] = original[1] * factor,
                ["Z"] = original[2] * factor,
            };
        }
    }

    /// <summary>
    /// This player's size, somewhere between the two ends. Derived rather than drawn, the same way
    /// the dyes are, so the same person is the same size tomorrow - and off the plain name and
    /// world rather than the outfit key, since changing job should not change anybody's body.
    /// </summary>
    private float Size(string playerKey, int roll)
    {
        var low = Math.Min(config.ShapeMin, config.ShapeMax);
        var high = Math.Max(config.ShapeMin, config.ShapeMax);

        return low + (high - low) * (Seed(playerKey, roll) / (float)uint.MaxValue);
    }

    /// <summary>Same hand-rolled hash as the dyes: String.GetHashCode is randomised per process
    /// and would give everybody a new body on every restart.</summary>
    private static uint Seed(string playerKey, int roll)
    {
        unchecked
        {
            var hash = 2166136261u;

            foreach (var c in playerKey)
                hash = (hash ^ c) * 16777619u;

            foreach (var b in BitConverter.GetBytes(roll))
                hash = (hash ^ b) * 16777619u;

            // Mixed once more, or a short name and a small roll leave the top bits barely
            // touched and everybody lands in the same corner of the range.
            hash ^= hash >> 15;
            return hash * 2246822519u;
        }
    }

    /// <summary>Forgets one person's shape, so the next pass gives them a fresh one.</summary>
    public void Forget(string playerKey) => given.Remove(playerKey);

    /// <summary>
    /// Lets go of the profiles of people who have long since walked away. Customize+ files them
    /// against the character and keeps them until it is told otherwise, which is exactly what
    /// makes a teleport free - but a long evening in a busy city would leave it holding hundreds
    /// of the things, and it walks that list every time anybody's bones are built.
    /// </summary>
    private void Prune()
    {
        if (DateTime.UtcNow < nextPrune)
            return;

        nextPrune = DateTime.UtcNow.AddMinutes(1);

        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var (key, had) in given.Where(g => g.Value.Seen < cutoff).ToList())
        {
            cplus.Release(had.Id);
            given.Remove(key);
        }
    }

    /// <summary>
    /// Reads the profile in again and hands everybody theirs again, for when it has been edited or
    /// swapped. Nothing is taken off first: Customize+ replaces a character's temporary profile as
    /// it files the new one, so going straight to the new one leaves no gap where they snap back.
    /// </summary>
    public void Reload()
    {
        bones = null;
        loaded = Guid.Empty;
        given.Clear();
        refused.Clear();
    }

    /// <summary>Takes everybody's back off.</summary>
    public void ReleaseAll()
    {
        // Customize+ having gone away has taken them with it, so there is nothing to ask it for
        // and no point complaining about each one in turn.
        if (wasAvailable)
            foreach (var (_, id, _, _) in given.Values)
                cplus.Release(id);

        given.Clear();
    }

    /// <summary>
    /// Customize+ restarting takes every temporary profile with it - they are never written to
    /// disk - so what we think people are wearing has to go with it, or nobody is ever given
    /// theirs again.
    /// </summary>
    public void Watch()
    {
        var available = cplus.Available;
        if (available && !wasAvailable && given.Count > 0)
        {
            Svc.Log.Information("[GlamRoulette] Customize+ restarted, handing the shapes out again");
            Reload();
        }

        wasAvailable = available;

        if (available)
            Prune();
    }
}
