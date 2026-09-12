using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ConfigLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace YeetReborn;

/// <summary>
/// Bridges the mod's client config to ConfigLib's in-game settings window.
/// <para>
/// Every member touches ConfigLib types, so the CLR only has to resolve configlib.dll when one
/// of them actually runs. <see cref="TryCreate"/> checks the mod is loaded before that can
/// happen and is marked NoInlining so the JIT cannot hoist the type reference into a caller
/// that runs unconditionally. Players without ConfigLib never load the assembly.
/// </para>
/// </summary>
public sealed class ConfigLibBridge
{
    const string Domain = "yeetreborn";
    const string VolumeCode = "VOLUME";
    const string PitchCode = "RANDOMIZE_PITCH";
    const string StrengthCode = "STRENGTH";
    const string LockAngleCode = "LOCK_ANGLE";
    const string ServerRangeCode = "SERVER_SOUND_RANGE";
    const string ServerCeilingCode = "SERVER_VOLUME_CEILING";
    const string ServerGruntCode = "SERVER_PLAY_GRUNT";
    const string ServerMaxStrengthCode = "SERVER_MAX_STRENGTH";

    static string SoundCode(string soundName) => $"SOUND_{soundName.ToUpperInvariant()}";

    readonly IConfigProvider provider;
    readonly IReadOnlyCollection<string> soundNames;
    readonly ILogger logger;

    /// <summary>
    /// Set while <see cref="Push"/> is writing. Each setting we write raises ConfigLib's
    /// SettingChanged, which would otherwise pull half-written state back over the values we are
    /// still in the middle of writing.
    /// </summary>
    bool pushing;

    ConfigLibBridge(IConfigProvider provider, IReadOnlyCollection<string> soundNames, ILogger logger)
    {
        this.provider = provider;
        this.soundNames = soundNames;
        this.logger = logger;
    }

    /// <summary>Returns null when ConfigLib is not installed or exposes no config for us.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static ConfigLibBridge? TryCreate(ICoreAPI api, IReadOnlyCollection<string> soundNames)
    {
        var system = api.ModLoader.GetModSystem<ConfigLibModSystem>();
        if (system == null) return null;

        return new ConfigLibBridge(system, soundNames, api.Logger);
    }

    /// <summary>
    /// Pulls ConfigLib's values into <paramref name="target"/> once configs are loaded, and again
    /// whenever the player changes one in the settings window.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Bind(YeetRebornClientConfig target, Action onChanged)
    {
        bool attached = false;

        void Sync()
        {
            if (pushing) return;
            if (!Pull(target)) return;
            onChanged();
            logger.Notification("[yeetreborn] ConfigLib sound pool: {0}",
                target.YeetSounds.Count == 0 ? "(none)" : string.Join(", ", target.YeetSounds));
        }

        void Attach()
        {
            if (attached) return;

            IConfig? config = provider.GetConfig(Domain);
            if (config == null) return;

            config.SettingChanged += _ => Sync();
            attached = true;
            Sync();
        }

        // ConfigLib raises ConfigsLoaded from its AssetsLoaded stage, which on the client can run
        // before StartClientSide - in which case subscribing here would be too late and we would
        // never see the player's ticked sounds. So try to attach immediately as well; whichever
        // path gets there first wins and the other becomes a no-op.
        provider.ConfigsLoaded += Attach;
        Attach();
    }

    /// <summary>
    /// Keeps <paramref name="target"/> in step with the server-side settings. Unlike the client
    /// settings these are never pushed back - the settings window is the only writer.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void BindServer(YeetRebornServerConfig target, Action onChanged)
    {
        bool attached = false;

        void Sync()
        {
            if (!PullServer(target)) return;
            onChanged();
            logger.Notification("[yeetreborn] ConfigLib server settings: range {0}, ceiling {1}, grunt {2}",
                target.YeetSoundRange, target.YeetSoundVolume, target.PlayGrunt);
        }

        void Attach()
        {
            if (attached) return;

            IConfig? config = provider.GetConfig(Domain);
            if (config == null) return;

            config.SettingChanged += _ => Sync();
            attached = true;
            Sync();
        }

        provider.ConfigsLoaded += Attach;
        Attach();
    }

    /// <summary>Reads the server-side settings into the config object.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool PullServer(YeetRebornServerConfig target)
    {
        IConfig? config = provider.GetConfig(Domain);
        if (config == null) return false;

        ISetting? range = config.GetSetting(ServerRangeCode);
        if (range != null) target.YeetSoundRange = range.Value.AsFloat(target.YeetSoundRange);

        ISetting? ceiling = config.GetSetting(ServerCeilingCode);
        if (ceiling != null) target.YeetSoundVolume = ceiling.Value.AsFloat(target.YeetSoundVolume);

        ISetting? grunt = config.GetSetting(ServerGruntCode);
        if (grunt != null) target.PlayGrunt = grunt.Value.AsBool(target.PlayGrunt);

        ISetting? maxStrength = config.GetSetting(ServerMaxStrengthCode);
        if (maxStrength != null) target.MaxYeetStrength = maxStrength.Value.AsFloat(target.MaxYeetStrength);

        return true;
    }

    /// <summary>Reads ConfigLib's settings into the config object. False if our config is absent.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Pull(YeetRebornClientConfig target)
    {
        IConfig? config = provider.GetConfig(Domain);
        if (config == null) return false;

        var enabled = new List<string>();
        foreach (string name in soundNames)
        {
            if (config.GetSetting(SoundCode(name))?.Value.AsBool(false) == true) enabled.Add(name);
        }
        target.YeetSounds = enabled;

        ISetting? volume = config.GetSetting(VolumeCode);
        if (volume != null) target.YeetVolume = volume.Value.AsInt(target.YeetVolume);

        ISetting? pitch = config.GetSetting(PitchCode);
        if (pitch != null) target.RandomizeYeetPitch = pitch.Value.AsBool(target.RandomizeYeetPitch);

        ISetting? strength = config.GetSetting(StrengthCode);
        if (strength != null) target.YeetStrength = strength.Value.AsFloat(target.YeetStrength);

        ISetting? lockAngle = config.GetSetting(LockAngleCode);
        if (lockAngle != null) target.LockLaunchAngle = lockAngle.Value.AsBool(target.LockLaunchAngle);

        return true;
    }

    /// <summary>
    /// Writes the config object back into ConfigLib and saves, so a change made by chat command
    /// shows up in the settings window rather than being overwritten by it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Push(YeetRebornClientConfig source)
    {
        IConfig? config = provider.GetConfig(Domain);
        if (config == null) return false;

        // Snapshot what we intend to write. source is the same object Pull writes into, so
        // reading it inside the loop would see values pulled back by our own change events.
        var desired = new List<string>(source.YeetSounds);
        int volume = source.YeetVolume;
        bool randomizePitch = source.RandomizeYeetPitch;
        float strength = source.YeetStrength;
        bool lockAngle = source.LockLaunchAngle;

        pushing = true;
        try
        {
            foreach (string name in soundNames)
            {
                Set(config, SoundCode(name), new JValue(desired.Contains(name)));
            }
            Set(config, VolumeCode, new JValue(volume));
            Set(config, PitchCode, new JValue(randomizePitch));
            Set(config, StrengthCode, new JValue(strength));
            Set(config, LockAngleCode, new JValue(lockAngle));
            config.WriteToFile();
        }
        finally
        {
            pushing = false;
        }

        logger.Notification("[yeetreborn] Wrote sound pool to ConfigLib: {0}",
            desired.Count == 0 ? "(none)" : string.Join(", ", desired));
        return true;
    }

    static void Set(IConfig config, string code, JValue value)
    {
        ISetting? setting = config.GetSetting(code);
        if (setting != null) setting.Value = new JsonObject(value);
    }
}
