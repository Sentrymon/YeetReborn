using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YeetReborn;

public class YeetRebornModSystem : ModSystem
{
    /// <summary>
    /// How fast motion decays per second, from PModuleMotionDrag scaling motion by
    /// 0.983^(dt*33) every step. Drag is exponential, so horizontal distance is the integral of
    /// a decaying exponential: distance = horizontalSpeed / this. That makes distance linear in
    /// launch speed, which is why strength can be a straight percentage of maximum distance.
    /// </summary>
    const double DragDecayPerSecond = 0.5658;

    /// <summary>
    /// Horizontal distance in blocks at 100% strength. Anchored so the mod's original throw
    /// (speed 70 at 45 degrees, about 87.5 blocks) sits at 50% strength.
    /// </summary>
    const double DistanceAtFullStrength = 175.0;

    /// <summary>
    /// Launch speed at 100% strength, in blocks/second: DistanceAtFullStrength * drag decay,
    /// divided by cos(45). Works out so that 50% strength is exactly the mod's original speed
    /// of 70.
    /// </summary>
    const double SpeedAtFullStrength = 140.0;

    const double LockedLaunchAngleRadians = GameMath.PI / 4;

    const float MinStrength = 25f;      // half the distance of the default
    const float MaxStrength = 100f;
    const float DefaultStrength = 50f;

    /// <summary>
    /// Simulation range for a yeeted item, in blocks. Vintage Story stops simulating an entity
    /// more than GlobalConstants.DefaultSimulationRange (128) blocks from any player - physics
    /// returns immediately once the entity goes Inactive, so an item thrown further than that
    /// froze in mid air. A yeet is explicitly meant to outrange that, so the item carries a
    /// wider simulation range of its own.
    /// </summary>
    const int YeetSimulationRange = 1024;

    /// <summary>
    /// Blocks of clearance kept below the world ceiling. At 45 degrees the apex of a throw equals
    /// its horizontal distance, so a hard yeet would otherwise leave the top of a 256 block world.
    /// Capping the vertical component alone flattens the arc while leaving the horizontal
    /// component - and therefore the distance - untouched.
    /// </summary>
    const double WorldCeilingMargin = 16;
    const double MinHeadroom = 8;

    const string ClientConfigFile = "yeetreborn-client.json";
    const string ServerConfigFile = "yeetreborn.json";
    const string DefaultSound = "Whoosh";

    static readonly AssetLocation GruntSound = new("game:sounds/player/strike");
    const float GruntRange = 50f;
    const float GruntVolume = 1f;

    /// <summary>
    /// Selectable yeet sounds, keyed by the name players use to pick one. A name maps to one or
    /// more variants; where there are several, one is rolled per throw. That is invisible to the
    /// player - Chicken is a single option in the list, not three.
    /// </summary>
    static readonly Dictionary<string, AssetLocation[]> YeetSounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Whoosh"] = new AssetLocation[] { new("game:sounds/player/throw") },
        ["Quack"] = new AssetLocation[] { new("yeetreborn:sounds/quack") },
        ["Yeet"] = new AssetLocation[] { new("yeetreborn:sounds/yeet") },
        ["Wilhelm"] = new AssetLocation[] { new("yeetreborn:sounds/wilhelm") },
        ["Aztec"] = new AssetLocation[] { new("yeetreborn:sounds/aztec") },
        ["Glass"] = new AssetLocation[] { new("yeetreborn:sounds/glass") },
        ["Chicken"] = new AssetLocation[]
        {
            new("yeetreborn:sounds/chicken1"),
            new("yeetreborn:sounds/chicken2"),
            new("yeetreborn:sounds/chicken3"),
        },
    };

    static string SoundNames => string.Join(", ", YeetSounds.Keys);

    ICoreServerAPI sapi = null!;
    ICoreClientAPI capi = null!;
    ConfigLibBridge? configLib;
    YeetRebornClientConfig clientConfig = new();
    YeetRebornServerConfig serverConfig = new();

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        capi = api;

        LoadClientConfig(api);

        // When ConfigLib is installed it owns these values; our JSON file is the fallback.
        if (api.ModLoader.IsModEnabled("configlib")) configLib = ConfigLibBridge.TryCreate(api, YeetSounds.Keys);
        configLib?.Bind(clientConfig, () => NormalizeClientConfig(api));

        api.Input.RegisterHotKey("yeetitem", "Yeet held item", GlKeys.Y, HotkeyType.CharacterControls);
        api.Input.RegisterHotKey("yeetstack", "Yeet held stack", GlKeys.Y, HotkeyType.CharacterControls, ctrlPressed: true);

        var channel = api.Network.RegisterChannel("yeetreborn").RegisterMessageType<YeetPacket>();

        api.Input.SetHotKeyHandler("yeetitem", _ =>
        {
            channel.SendPacket(new YeetPacket
            {
                WholeStack = false,
                Sound = RollSound(),
                Volume = clientConfig.YeetVolume,
                RandomizePitch = clientConfig.RandomizeYeetPitch,
                Strength = clientConfig.YeetStrength,
                LockLaunchAngle = clientConfig.LockLaunchAngle,
            });
            return true;
        });
        api.Input.SetHotKeyHandler("yeetstack", _ =>
        {
            channel.SendPacket(new YeetPacket
            {
                WholeStack = true,
                Sound = RollSound(),
                Volume = clientConfig.YeetVolume,
                RandomizePitch = clientConfig.RandomizeYeetPitch,
                Strength = clientConfig.YeetStrength,
                LockLaunchAngle = clientConfig.LockLaunchAngle,
            });
            return true;
        });

        RegisterHelpCommand(api);
        RegisterSoundCommand(api);
        RegisterVolumeCommand(api);
        RegisterStrengthCommand(api);
        RegisterAngleCommand(api);
        RegisterPitchCommand(api);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        sapi = api;

        LoadServerConfig(api);

        // When ConfigLib is installed server-side it owns these values; our JSON file is the fallback.
        if (api.ModLoader.IsModEnabled("configlib")) ConfigLibBridge.TryCreate(api, YeetSounds.Keys)?.BindServer(serverConfig, () => NormalizeServerConfig(api));

        api.Network.RegisterChannel("yeetreborn")
            .RegisterMessageType<YeetPacket>()
            .SetMessageHandler<YeetPacket>(OnYeetPacket);
    }

    void OnYeetPacket(IServerPlayer fromPlayer, YeetPacket packet)
    {
        ItemSlot slot = fromPlayer.InventoryManager.ActiveHotbarSlot;
        if (slot == null || slot.Empty) return;

        ItemStack? stack = packet.WholeStack ? slot.TakeOutWhole() : slot.TakeOut(1);
        if (stack == null) return;
        slot.MarkDirty();

        EntityPlayer entityPlayer = fromPlayer.Entity;
        Vec3d viewDir = entityPlayer.Pos.GetViewVector().ToVec3d();
        Vec3d horizontalDir = new Vec3d(viewDir.X, 0, viewDir.Z).Normalize();

        // Locked throws always leave at 45 degrees; unlocked ones follow where the player looks.
        // viewDir is a unit vector, so its Y component is sin(pitch).
        double launchAngle = packet.LockLaunchAngle
            ? LockedLaunchAngleRadians
            : Math.Asin(GameMath.Clamp(viewDir.Y, -1.0, 1.0));

        Vec3d dir = (horizontalDir * Math.Cos(launchAngle)).Add(0, Math.Sin(launchAngle), 0);

        // packet.Strength is untrusted: clamp to the valid range, then to the server's ceiling.
        float strength = GameMath.Clamp(packet.Strength, MinStrength, MaxStrength);
        strength = Math.Min(strength, serverConfig.MaxYeetStrength);

        Vec3d spawnPos = entityPlayer.Pos.XYZ.Add(0, entityPlayer.LocalEyePos.Y, 0).Add(dir);
        Vec3d velocity = dir * (SpeedAtFullStrength * strength / 100.0); // blocks/second

        double launchY = entityPlayer.Pos.Y + entityPlayer.LocalEyePos.Y;
        double headroom = Math.Max(MinHeadroom, sapi.WorldManager.MapSizeY - launchY - WorldCeilingMargin);
        velocity.Y = Math.Min(velocity.Y, headroom * DragDecayPerSecond);


        var itemEntity = sapi.World.SpawnItemEntity(stack, spawnPos, velocity / 60.0);
        if (itemEntity == null) return;

        // Without this, the item has no anti-instant-repickup grace period (normally set by
        // regular item-drop code) and gets vacuumed right back into the hotbar by VintageStory's
        // walk-over auto-pickup before it can visibly fly away.
        if (itemEntity is EntityItem entityItem) entityItem.ByPlayerUid = fromPlayer.PlayerUID;

        // Keep simulating the item well past the default 128 block range, or it stops mid flight.
        itemEntity.SimulationRange = YeetSimulationRange;

        var flightBehavior = new EntityBehaviorYeetFlight(itemEntity);
        flightBehavior.Init(sapi);
        itemEntity.AddBehavior(flightBehavior);

        if (serverConfig.PlayGrunt)
        {
            sapi.World.PlaySoundAt(GruntSound, entityPlayer, null, true, GruntRange, GruntVolume);
        }

        // packet.Sound comes off the wire, so never index the map with it directly.
        if (!YeetSounds.TryGetValue(packet.Sound ?? DefaultSound, out AssetLocation[]? variants))
        {
            variants = YeetSounds[DefaultSound];
        }

        // A sound with several variants rolls one per throw, server-side, so every player in
        // earshot hears the same variant for this throw.
        AssetLocation sound = variants.Length == 1
            ? variants[0]
            : variants[sapi.World.Rand.Next(variants.Length)];
        float volume = GameMath.Clamp(packet.Volume, 0, 100) / 100f
            * serverConfig.YeetSoundVolume;
        if (volume <= 0f) return;

        sapi.World.PlaySoundAt(sound, entityPlayer, null, packet.RandomizePitch, serverConfig.YeetSoundRange, volume);
    }

    /// <summary>
    /// Picks one sound from the player's pool. Several ticked means a different one per throw;
    /// one ticked behaves exactly like a fixed choice.
    /// </summary>
    string RollSound()
    {
        List<string> pool = clientConfig.YeetSounds;
        return pool.Count switch
        {
            0 => DefaultSound,
            1 => pool[0],
            _ => pool[capi.World.Rand.Next(pool.Count)],
        };
    }

    /// <summary>Persists a settings change to whichever config currently owns it.</summary>
    void SaveClientConfig(ICoreClientAPI api)
    {
        if (configLib?.Push(clientConfig) == true) return;
        api.StoreModConfig(clientConfig, ClientConfigFile);
    }

    void RegisterHelpCommand(ICoreClientAPI api)
    {
        // WithRootAlias, not WithAlias: the latter registers a subcommand alias. It must come
        // after HandleWith so the handler lands on the root command rather than the alias.
        api.ChatCommands.Create("yeet")
            .WithDescription("List the YeetReborn commands.")
            .HandleWith(_ => TextCommandResult.Success(HelpText()))
            .WithRootAlias("yeethelp");
    }

    /// <summary>
    /// Chat output is parsed as VTML, so a raw angle bracket aborts the parse and the whole
    /// message renders as nothing. Any &lt; or &gt; here must stay escaped.
    /// <para>
    /// Columns are separated by " - " rather than padded with spaces: the chat font is
    /// proportional, VTML has no table or tab tag, and the game ships no monospace font, so
    /// aligned columns are not achievable.
    /// </para>
    /// </summary>
    string HelpText()
    {
        var text = new StringBuilder("YeetReborn commands:");
        text.AppendLine().Append("  .yeet - show this help list");
        text.AppendLine().Append("  .yeetsound - show sound settings");
        text.AppendLine().Append("  .yeetsound &lt;options&gt; - Separate options with a space. eg; .yeetsound quack glass");
        text.AppendLine().Append("  .yeetvol - show volume settings");
        text.AppendLine().Append("  .yeetvol 60 - set volume 0-100 %");
        text.AppendLine().Append("  .yeetstrength - show how far your yeets go");
        text.AppendLine().Append("  .yeetstrength 40 - set distance 7.5-100 %");
        text.AppendLine().Append("  .yeetlock - show whether throws are locked to 45 degrees");
        text.AppendLine().Append("  .yeetlock off - throw where you look instead");
        text.AppendLine().Append("  .yeetpitch - Show pitch settings");
        text.AppendLine().Append("  .yeetpitch on - turn random pitch on or off");

        return text.ToString();
    }

    void RegisterSoundCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("yeetsound")
            .WithDescription($"Choose your yeet sounds. Name several to have one picked at random per throw. One of: {SoundNames}")
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("sounds"))
            .HandleWith(args =>
            {
                var requested = args[0] as string;
                if (string.IsNullOrWhiteSpace(requested))
                {
                    return TextCommandResult.Success(DescribeSounds());
                }

                var chosen = new List<string>();
                foreach (string word in requested.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // Echo back the map's own casing rather than whatever the player typed.
                    string? match = YeetSounds.Keys.FirstOrDefault(
                        name => name.Equals(word, StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        return TextCommandResult.Error($"No such yeet sound '{word}'. Available: {SoundNames}");
                    }
                    if (!chosen.Contains(match)) chosen.Add(match);
                }

                clientConfig.YeetSounds = chosen;
                SaveClientConfig(api);
                return TextCommandResult.Success(DescribeSounds());
            });
    }

    /// <summary>Two lines: everything available, and what the player currently has selected.</summary>
    string DescribeSounds()
    {
        string selected = clientConfig.YeetSounds.Count == 0
            ? "(none)"
            : string.Join(" ", clientConfig.YeetSounds);

        var text = new StringBuilder("Available Options: ");
        text.Append(string.Join(" ", YeetSounds.Keys)).AppendLine();
        text.Append("Current Selected: ").Append(selected);
        return text.ToString();
    }

    void RegisterVolumeCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("yeetvol")
            .WithDescription("Set your yeet sound volume, 0-100.")
            .WithArgs(api.ChatCommands.Parsers.OptionalInt("percent"))
            .HandleWith(args =>
            {
                if (args[0] is not int requested)
                {
                    return TextCommandResult.Success($"Yeet volume is {clientConfig.YeetVolume}%.");
                }

                if (requested < 0 || requested > 100)
                {
                    return TextCommandResult.Error($"Yeet volume must be between 0 and 100, got {requested}.");
                }

                clientConfig.YeetVolume = requested;
                SaveClientConfig(api);
                return TextCommandResult.Success(requested == 0
                    ? "Yeet volume set to 0% - your yeets are silent."
                    : $"Yeet volume set to {requested}%.");
            });
    }

    void RegisterStrengthCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("yeetstrength")
            .WithDescription($"Set how far your yeets go, {MinStrength} to {MaxStrength} percent of maximum distance.")
            .WithArgs(api.ChatCommands.Parsers.OptionalFloat("percent", -1f))
            .HandleWith(args =>
            {
                // -1 is the sentinel for "no argument given"; it is outside the valid range.
                float requested = (float)args[0];
                if (requested < 0f)
                {
                    return TextCommandResult.Success(
                        $"Yeet strength is {clientConfig.YeetStrength}% (default {DefaultStrength}%).");
                }

                if (requested < MinStrength || requested > MaxStrength)
                {
                    return TextCommandResult.Error(
                        $"Yeet strength must be between {MinStrength} and {MaxStrength}, got {requested}.");
                }

                clientConfig.YeetStrength = requested;
                SaveClientConfig(api);
                return TextCommandResult.Success($"Yeet strength set to {requested}%.");
            });
    }

    void RegisterAngleCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("yeetlock")
            .WithDescription("Lock throws to a fixed 45 degree arc: on or off. Off throws where you look.")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("state"))
            .HandleWith(args =>
            {
                var requested = args[0] as string;
                if (string.IsNullOrWhiteSpace(requested))
                {
                    return TextCommandResult.Success(
                        $"Locked 45 degree throws are {(clientConfig.LockLaunchAngle ? "on" : "off")}.");
                }

                bool enabled;
                switch (requested.ToLowerInvariant())
                {
                    case "on" or "true" or "yes" or "1":
                        enabled = true;
                        break;
                    case "off" or "false" or "no" or "0":
                        enabled = false;
                        break;
                    default:
                        return TextCommandResult.Error($"Expected 'on' or 'off', got '{requested}'.");
                }

                clientConfig.LockLaunchAngle = enabled;
                SaveClientConfig(api);
                return TextCommandResult.Success(enabled
                    ? "Throws locked to 45 degrees."
                    : "Throws now follow where you look.");
            });
    }

    void RegisterPitchCommand(ICoreClientAPI api)
    {
        api.ChatCommands.Create("yeetpitch")
            .WithDescription("Randomise your yeet sound's pitch on each throw: on or off.")
            .WithArgs(api.ChatCommands.Parsers.OptionalWord("state"))
            .HandleWith(args =>
            {
                var requested = args[0] as string;
                if (string.IsNullOrWhiteSpace(requested))
                {
                    return TextCommandResult.Success(
                        $"Yeet pitch randomisation is {(clientConfig.RandomizeYeetPitch ? "on" : "off")}.");
                }

                bool enabled;
                switch (requested.ToLowerInvariant())
                {
                    case "on" or "true" or "yes" or "1":
                        enabled = true;
                        break;
                    case "off" or "false" or "no" or "0":
                        enabled = false;
                        break;
                    default:
                        return TextCommandResult.Error($"Expected 'on' or 'off', got '{requested}'.");
                }

                clientConfig.RandomizeYeetPitch = enabled;
                SaveClientConfig(api);
                return TextCommandResult.Success($"Yeet pitch randomisation {(enabled ? "on" : "off")}.");
            });
    }

    void LoadClientConfig(ICoreClientAPI api)
    {
        try
        {
            clientConfig = api.LoadModConfig<YeetRebornClientConfig>(ClientConfigFile) ?? new YeetRebornClientConfig();
        }
        catch (Exception e)
        {
            api.Logger.Error("[yeetreborn] Could not read {0}, using defaults: {1}", ClientConfigFile, e.Message);
            clientConfig = new YeetRebornClientConfig();
        }

        // Pre-1.5.0 files carry a single YeetSound instead of a pool.
        if (clientConfig.YeetSound != null)
        {
            api.Logger.Notification("[yeetreborn] Migrating YeetSound '{0}' in {1} to the sound pool.",
                clientConfig.YeetSound, ClientConfigFile);
            clientConfig.YeetSounds = new List<string> { clientConfig.YeetSound };
            clientConfig.YeetSound = null;
        }

        NormalizeClientConfig(api);
        api.StoreModConfig(clientConfig, ClientConfigFile);
    }

    /// <summary>
    /// Drops sound names we do not recognise and clamps the volume. Runs on load and again on
    /// every ConfigLib change, since both are outside our control.
    /// </summary>
    void NormalizeClientConfig(ICoreClientAPI api)
    {
        var valid = new List<string>();
        foreach (string name in clientConfig.YeetSounds)
        {
            string? match = YeetSounds.Keys.FirstOrDefault(
                known => known.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                api.Logger.Warning("[yeetreborn] Unknown yeet sound \"{0}\", ignoring. Valid values: {1}.",
                    name, SoundNames);
                continue;
            }
            if (!valid.Contains(match)) valid.Add(match);
        }
        clientConfig.YeetSounds = valid;

        if (clientConfig.YeetVolume < 0 || clientConfig.YeetVolume > 100)
        {
            api.Logger.Warning("[yeetreborn] YeetVolume {0} is outside 0-100, clamping.", clientConfig.YeetVolume);
            clientConfig.YeetVolume = GameMath.Clamp(clientConfig.YeetVolume, 0, 100);
        }

        // The scale changed in build 1.1.22: the original throw moved from 15% to 50%. Anything
        // below the new minimum is a value from the old scale, so restore the default rather
        // than clamping it to half distance.
        if (clientConfig.YeetStrength < MinStrength)
        {
            clientConfig.YeetStrength = DefaultStrength;
        }

        float strength = GameMath.Clamp(clientConfig.YeetStrength, MinStrength, MaxStrength);
        if (!clientConfig.YeetStrength.Equals(strength))
        {
            api.Logger.Warning("[yeetreborn] YeetStrength {0} is outside {1}-{2}, clamping to {3}.",
                clientConfig.YeetStrength, MinStrength, MaxStrength, strength);
            clientConfig.YeetStrength = strength;
        }
    }

    void LoadServerConfig(ICoreServerAPI api)
    {
        try
        {
            serverConfig = api.LoadModConfig<YeetRebornServerConfig>(ServerConfigFile) ?? new YeetRebornServerConfig();
        }
        catch (Exception e)
        {
            api.Logger.Error("[yeetreborn] Could not read {0}, using defaults: {1}", ServerConfigFile, e.Message);
            serverConfig = new YeetRebornServerConfig();
        }

        NormalizeServerConfig(api);
        api.StoreModConfig(serverConfig, ServerConfigFile);
    }

    /// <summary>
    /// Clamps the server values into range. Runs on load and again on every ConfigLib change,
    /// since both are outside our control.
    /// </summary>
    void NormalizeServerConfig(ICoreServerAPI api)
    {
        // Volume was a raw PlaySoundAt argument before build 1.1.3, where values above 1 were
        // silently clamped by the engine. Normalise any such legacy value so the file reflects
        // what actually happens.
        float ceiling = GameMath.Clamp(serverConfig.YeetSoundVolume, 0f, 1f);
        if (!serverConfig.YeetSoundVolume.Equals(ceiling))
        {
            api.Logger.Warning("[yeetreborn] YeetSoundVolume {0} is outside 0-1, clamping to {1}.",
                serverConfig.YeetSoundVolume, ceiling);
            serverConfig.YeetSoundVolume = ceiling;
        }

        float maxStrength = GameMath.Clamp(serverConfig.MaxYeetStrength, MinStrength, MaxStrength);
        if (!serverConfig.MaxYeetStrength.Equals(maxStrength))
        {
            api.Logger.Warning("[yeetreborn] MaxYeetStrength {0} is outside {1}-{2}, clamping to {3}.",
                serverConfig.MaxYeetStrength, MinStrength, MaxStrength, maxStrength);
            serverConfig.MaxYeetStrength = maxStrength;
        }

        float range = GameMath.Clamp(serverConfig.YeetSoundRange, 1f, 64f);
        if (!serverConfig.YeetSoundRange.Equals(range))
        {
            api.Logger.Warning("[yeetreborn] YeetSoundRange {0} is outside 1-64, clamping to {1}.",
                serverConfig.YeetSoundRange, range);
            serverConfig.YeetSoundRange = range;
        }
    }
}
