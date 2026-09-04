using System.Diagnostics;
using System.Text.Json;

namespace IsaacProfileManager.SteamHelper;

/// <summary>
/// Subscribes to Workshop items, waits for Steam to put them on disk, and says
/// where they landed — then, on a second invocation, unsubscribes again.
///
/// It is a separate process because Isaac's steam_api.dll is 32-bit and the app
/// is 64-bit. It is a separate *invocation* for the unsubscribe because Steam
/// deletes content shortly after a subscription is dropped: the parent copies
/// what it wants first, then asks for the cleanup.
///
/// Everything it says is one JSON object per line on stdout, so the parent can
/// stream progress instead of waiting for a gigabyte of downloads in silence.
/// </summary>
public static class Program
{
    private const int PollMilliseconds = 200;

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (SteamHelperException ex)
        {
            Emit("error", new() { ["message"] = ex.Message });
            return 1;
        }
        catch (Exception ex)
        {
            Emit("error", new() { ["message"] = $"{ex.GetType().Name}: {ex.Message}" });
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var options = Options.Parse(args);

        using var steam = SteamUgc.Connect(options.GameDir);

        var owns = steam.OwnsApp();
        Emit("ready", new()
        {
            ["subscribed"] = steam.SubscribedCount(),
            ["ownsApp"] = owns,
            ["loggedOn"] = steam.IsLoggedOn(),
        });

        return options.Verb switch
        {
            "status" => Done(true),
            "pull" => Pull(steam, options),
            "subscribe" => SubscribeOnly(steam, options),
            "unsubscribe" => Unsubscribe(steam, options.Ids, options),
            "unsubscribe-all" => Unsubscribe(steam, steam.SubscribedItems(), options),
            "cloud-list" => CloudList(steam),
            "cloud-replace" => CloudReplace(steam, options),
            _ => throw new SteamHelperException(
                $"Unknown verb '{options.Verb}'. Expected status, pull, subscribe, unsubscribe, unsubscribe-all, cloud-list or cloud-replace."),
        };
    }

    // --- Saves through Steam's own API ---------------------------------------
    // The game reads its saves through ISteamRemoteStorage, and Steam only
    // answers for files it has indexed. A file copied into the folder can be
    // invisible to it. These verbs write and delete through the API so the
    // index is right by construction; the parent verifies each write by
    // reading it back the same way the game will.

    private static int CloudList(SteamUgc steam)
    {
        Emit("cloud", new() { ["enabled"] = steam.CloudEnabledForApp(), ["storage"] = steam.HasRemoteStorage });
        foreach (var (name, size, persisted) in steam.CloudFiles())
            Emit("file", new() { ["name"] = name, ["size"] = size, ["persisted"] = persisted });
        return Done(true);
    }

    /// <summary>
    /// Delete the named files, then write the named files from a folder.
    /// Deletes first so a name in both lists ends up written. Each write is
    /// read back through the API and compared, because "FileWrite returned
    /// true" is not the same as "the game will find it".
    /// </summary>
    private static int CloudReplace(SteamUgc steam, Options options)
    {
        if (!steam.HasRemoteStorage)
            throw new SteamHelperException("Steam did not expose ISteamRemoteStorage, so saves cannot be written through it.");

        var ok = true;
        foreach (var name in options.DeleteNames)
        {
            var existed = steam.CloudFileExists(name);
            var deleted = !existed || steam.CloudDelete(name);
            Emit("deleted", new() { ["name"] = name, ["existed"] = existed, ["ok"] = deleted });
            ok &= deleted;
        }

        foreach (var name in options.WriteNames)
        {
            var source = Path.Combine(options.From, name);
            if (!File.Exists(source))
            {
                Emit("written", new() { ["name"] = name, ["ok"] = false, ["reason"] = "source missing" });
                ok = false;
                continue;
            }

            var data = File.ReadAllBytes(source);
            var wrote = steam.CloudWrite(name, data);
            var back = wrote ? steam.CloudRead(name) : null;
            var verified = back is not null && back.AsSpan().SequenceEqual(data);
            Emit("written", new()
            {
                ["name"] = name,
                ["bytes"] = data.Length,
                ["ok"] = verified,
                ["reason"] = !wrote ? "FileWrite refused" : !verified ? "read back differs" : null,
            });
            ok &= verified;
        }

        steam.RunCallbacks();
        return Done(ok);
    }

    /// <summary>
    /// Subscribe to every requested item and wait for Steam to finish placing it.
    ///
    /// The settle window exists because state right after subscribing still
    /// describes the previous revision: an item can read Installed for a moment
    /// before Steam flags the pending download, and accepting that would hand the
    /// parent stale content to import.
    /// </summary>
    private static int Pull(SteamUgc steam, Options options)
    {
        // Steam only allows Workshop subscriptions for a game the account owns.
        // Without this check the run looks identical to a slow download and
        // then times out with nothing to say, which is exactly how it was first
        // reported: "it is not downloading them".
        if (steam.OwnsApp() == false)
        {
            Emit("error", new()
            {
                ["message"] = "This Steam account does not own The Binding of Isaac: Rebirth, so Steam will not " +
                              "let it subscribe to Workshop items. Sign in to the account that owns the game.",
            });
            return 1;
        }

        var pending = new Dictionary<ulong, Stopwatch>();

        foreach (var id in options.Ids)
        {
            steam.Subscribe(id);
            steam.Download(id);
            pending[id] = Stopwatch.StartNew();
            Emit("subscribed", new() { ["id"] = id.ToString() });
        }

        // A subscribe is a request, not a result. Pump callbacks briefly and then
        // ask Steam which items it actually holds, so an id that was rejected is
        // named now rather than after the download timeout expires.
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < TimeSpan.FromSeconds(3))
        {
            steam.RunCallbacks();
            Thread.Sleep(PollMilliseconds);
        }

        var registered = steam.SubscribedItems().ToHashSet();
        foreach (var id in options.Ids.Where(id => !registered.Contains(id)))
            Emit("warning", new()
            {
                ["id"] = id.ToString(),
                ["message"] = "Steam did not register a subscription for this item. It may have been removed from " +
                              "the Workshop, or be unavailable to this account.",
            });

        var deadline = Stopwatch.StartNew();
        var reported = new Dictionary<ulong, ulong>();
        var incomplete = false;

        while (pending.Count > 0)
        {
            steam.RunCallbacks();
            Thread.Sleep(PollMilliseconds);

            foreach (var (id, since) in pending.ToList())
            {
                var state = steam.State(id);
                var busy = (state & (ItemState.Downloading | ItemState.DownloadPending | ItemState.NeedsUpdate)) != 0;

                if ((state & ItemState.Downloading) != 0)
                {
                    var (downloaded, total) = steam.DownloadProgress(id);
                    if (total > 0 && reported.GetValueOrDefault(id) != downloaded)
                    {
                        reported[id] = downloaded;
                        Emit("progress", new()
                        {
                            ["id"] = id.ToString(),
                            ["downloaded"] = downloaded,
                            ["total"] = total,
                        });
                    }
                }

                if (!busy && (state & ItemState.Installed) != 0 && since.Elapsed >= options.Settle)
                {
                    pending.Remove(id);
                    var info = steam.InstallInfo(id);

                    if (info is null)
                    {
                        incomplete = true;
                        Emit("item", new() { ["id"] = id.ToString(), ["state"] = "missing" });
                        continue;
                    }

                    Emit("item", new()
                    {
                        ["id"] = id.ToString(),
                        ["state"] = "installed",
                        ["path"] = info.Value.Folder,
                        ["size"] = info.Value.SizeOnDisk,
                        ["timestamp"] = info.Value.Timestamp,
                    });
                    continue;
                }

                // An item Steam never acknowledged stays at None forever. Waiting
                // the full download timeout for it tells the user nothing and
                // looks like a hang, so cut it loose early and say why.
                // Only items Steam never acknowledged. An item queued behind 24
                // others reports DownloadPending, not None, so a slow queue is
                // never mistaken for a rejected subscription.
                if (state == ItemState.None && !registered.Contains(id) && since.Elapsed >= options.Stall)
                {
                    pending.Remove(id);
                    incomplete = true;
                    Emit("item", new()
                    {
                        ["id"] = id.ToString(),
                        ["state"] = "not-subscribed",
                        ["itemState"] = state.ToString(),
                    });
                    continue;
                }

                if (deadline.Elapsed < options.Timeout) continue;

                pending.Remove(id);
                incomplete = true;
                Emit("item", new()
                {
                    ["id"] = id.ToString(),
                    ["state"] = "timeout",
                    ["itemState"] = state.ToString(),
                });
            }
        }

        // Deliberately still subscribed. The parent copies the content out and
        // then calls the unsubscribe verb; dropping it here would race Steam's
        // cleanup of the very folder we just pointed at.
        return Done(!incomplete);
    }

    /// <summary>
    /// Subscribe and stop there, leaving Steam to download in its own time.
    ///
    /// The pull verb owns the whole cycle and hands content back for the library;
    /// this is the escape hatch for when that is not what you want. Whatever is
    /// subscribed here stays subscribed, so Steam treats these mods normally --
    /// which also means they will be laid into the active profile on the next
    /// launch, exactly like any ordinary subscription.
    /// </summary>
    private static int SubscribeOnly(SteamUgc steam, Options options)
    {
        if (steam.OwnsApp() == false)
        {
            Emit("error", new()
            {
                ["message"] = "This Steam account does not own The Binding of Isaac: Rebirth, so Steam will not " +
                              "let it subscribe to Workshop items. Sign in to the account that owns the game.",
            });
            return 1;
        }

        foreach (var id in options.Ids)
        {
            steam.Subscribe(id);
            steam.Download(id);
            Emit("subscribed", new() { ["id"] = id.ToString() });
        }

        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < options.Settle)
        {
            steam.RunCallbacks();
            Thread.Sleep(PollMilliseconds);
        }

        // Say which ones Steam actually took, so a silent rejection is visible
        // here rather than as a mod that never appears.
        var registered = steam.SubscribedItems().ToHashSet();
        var refused = options.Ids.Where(id => !registered.Contains(id)).ToList();

        foreach (var id in refused)
            Emit("warning", new()
            {
                ["id"] = id.ToString(),
                ["message"] = "Steam did not register a subscription for this item.",
            });

        Emit("ready", new() { ["subscribed"] = steam.SubscribedCount() });
        return Done(refused.Count == 0);
    }

    private static int Unsubscribe(SteamUgc steam, IReadOnlyList<ulong> ids, Options options)
    {
        foreach (var id in ids)
        {
            steam.Unsubscribe(id);
            Emit("unsubscribed", new() { ["id"] = id.ToString() });
        }

        // Unsubscribing is an async job. Pump long enough for it to be sent,
        // otherwise shutting down here cancels it and the subscription survives.
        var settle = Stopwatch.StartNew();
        while (settle.Elapsed < options.Settle)
        {
            steam.RunCallbacks();
            Thread.Sleep(PollMilliseconds);
        }

        Emit("ready", new() { ["subscribed"] = steam.SubscribedCount() });
        return Done(true);
    }

    private static int Done(bool ok)
    {
        Emit("done", new() { ["ok"] = ok });
        return ok ? 0 : 2;
    }

    /// <summary>
    /// One JSON object per line, written by hand rather than by the reflection
    /// serializer. That is what lets this ship trimmed: a 60 MB helper beside a
    /// 160 MB app is most of an install for a few hundred lines of code.
    /// </summary>
    private static void Emit(string name, Dictionary<string, object?> fields)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event", name);

            foreach (var (key, value) in fields)
            {
                switch (value)
                {
                    case null: writer.WriteNull(key); break;
                    case string text: writer.WriteString(key, text); break;
                    case bool flag: writer.WriteBoolean(key, flag); break;
                    case ulong number: writer.WriteNumber(key, number); break;
                    case uint number: writer.WriteNumber(key, number); break;
                    case long number: writer.WriteNumber(key, number); break;
                    case int number: writer.WriteNumber(key, number); break;
                    default: writer.WriteString(key, value.ToString()); break;
                }
            }

            writer.WriteEndObject();
        }

        Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        Console.Out.Flush();
    }

    private sealed class Options
    {
        public string Verb { get; private set; } = "status";
        public string GameDir { get; private set; } = string.Empty;
        public TimeSpan Timeout { get; private set; } = TimeSpan.FromMinutes(10);
        public TimeSpan Settle { get; private set; } = TimeSpan.FromSeconds(5);

        /// <summary>How long an item may sit in state None before it is given up on.</summary>
        public TimeSpan Stall { get; private set; } = TimeSpan.FromSeconds(45);
        public List<ulong> Ids { get; } = new();

        /// <summary>cloud-replace: the folder holding the files to write, and the names to delete and write.</summary>
        public string From { get; private set; } = string.Empty;
        public List<string> DeleteNames { get; } = new();
        public List<string> WriteNames { get; } = new();

        public static Options Parse(string[] args)
        {
            if (args.Length == 0)
                throw new SteamHelperException(
                    "Usage: ipm-steam-helper <status|pull|subscribe|unsubscribe|unsubscribe-all> " +
                    "--game-dir <path> [--timeout <s>] [--settle <s>] <id>...");

            var options = new Options { Verb = args[0].ToLowerInvariant() };

            for (var i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--game-dir":
                        options.GameDir = Next(args, ref i, "--game-dir");
                        break;
                    case "--timeout":
                        options.Timeout = TimeSpan.FromSeconds(ParseSeconds(Next(args, ref i, "--timeout")));
                        break;
                    case "--settle":
                        options.Settle = TimeSpan.FromSeconds(ParseSeconds(Next(args, ref i, "--settle")));
                        break;
                    case "--stall":
                        options.Stall = TimeSpan.FromSeconds(ParseSeconds(Next(args, ref i, "--stall")));
                        break;
                    case "--from":
                        options.From = Next(args, ref i, "--from");
                        break;
                    case "--delete":
                        options.DeleteNames.Add(SaveName(Next(args, ref i, "--delete")));
                        break;
                    case "--write":
                        options.WriteNames.Add(SaveName(Next(args, ref i, "--write")));
                        break;
                    default:
                        if (!ulong.TryParse(args[i], out var id))
                            throw new SteamHelperException($"'{args[i]}' is not a published file id.");
                        options.Ids.Add(id);
                        break;
                }
            }

            if (options.GameDir.Length == 0)
                throw new SteamHelperException("--game-dir is required.");
            if (options.Verb is "cloud-replace")
            {
                if (options.WriteNames.Count > 0 && options.From.Length == 0)
                    throw new SteamHelperException("cloud-replace --write needs --from <folder>.");
                if (options.WriteNames.Count == 0 && options.DeleteNames.Count == 0)
                    throw new SteamHelperException("cloud-replace needs at least one --delete or --write.");
            }
            else if (options.Verb is not ("status" or "unsubscribe-all" or "cloud-list") && options.Ids.Count == 0)
                throw new SteamHelperException($"'{options.Verb}' needs at least one published file id.");

            return options;
        }

        /// <summary>A bare file name: the API addresses files by name inside the app's folder, never by path.</summary>
        private static string SaveName(string value)
        {
            if (value.Length == 0 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                value.Contains('/') || value.Contains('\\') || value == "." || value == "..")
                throw new SteamHelperException($"'{value}' is not a save file name.");
            return value;
        }

        private static string Next(string[] args, ref int i, string flag) =>
            ++i < args.Length ? args[i] : throw new SteamHelperException($"{flag} needs a value.");

        private static double ParseSeconds(string value) =>
            double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds
                : throw new SteamHelperException($"'{value}' is not a number of seconds.");
    }
}
