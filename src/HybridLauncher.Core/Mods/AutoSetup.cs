using HybridLauncher.Core.Opti;

namespace HybridLauncher.Core.Mods;

/// <summary>What the Auto tab does with one mod: put it in, it is already there, it is replaced by the build picked, it cannot go
/// in this game, another pick already brings it.</summary>
public enum AutoState { Install, Installed, Update, NotPossible, Covered }

/// <summary>
/// A RenoDX or Luma mod listed for the game, as the Auto tab needs it. <paramref name="Engine"/> is set for a generic mod made
/// for every game of an engine (Unreal, Unity), not for this one; <paramref name="Tested"/> says the list names this game
/// among the ones it was tried with.
/// </summary>
public sealed record AutoCandidate(string Name, bool Working, bool HasDownload, string? Engine = null, bool Tested = false)
{
    public bool Generic => Engine is not null;
}

/// <summary>
/// What the Auto tab knows about a game before it plans (read by <see cref="AutoSetup.ReadFolder"/> and the page): the exe's
/// bitness, the graphics APIs, the card, the display, what is in the folder, and what the mod lists offer.
/// </summary>
public sealed class AutoFacts
{
    public bool Is64 { get; init; } = true;
    /// <summary>"DX9", "DX11", "DX12", "Vulkan", "OpenGL"…; empty when not known.</summary>
    public IReadOnlyList<string> Apis { get; init; } = [];
    public bool Nvidia { get; init; }
    /// <summary>What DLSS 5 can run on with this PC's card.</summary>
    public Dlss5Gpu Dlss5Tier { get; init; } = Dlss5Gpu.Unknown;
    /// <summary>A display of this PC has HDR on (RenoDX is mainly for HDR, and Luma mods often add it).</summary>
    public bool Hdr { get; init; }
    /// <summary>Proxy dll names in the folder and what they are: "opti", "reshade", "layer" (DXVK, VKD3D-Proton), "ultra", "other".</summary>
    public IReadOnlyDictionary<string, string> Files { get => _files; init => _files = value; }
    IReadOnlyDictionary<string, string> _files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same facts with more names held (a layer about to go in), so the other mods' names avoid them.</summary>
    public AutoFacts WithTaken(IEnumerable<string> names, string what)
    {
        var copy = (AutoFacts)MemberwiseClone();
        var files = new Dictionary<string, string>(_files, StringComparer.OrdinalIgnoreCase);
        foreach (var n in names) files.TryAdd(n, what);
        copy._files = files;
        return copy;
    }
    /// <summary>OptiScaler's dll in the folder, or null.</summary>
    public string? OptiFile { get; init; }
    /// <summary>The OptiScaler in the folder is a DLSS NR build (janblade's F5 and the like).</summary>
    public bool OptiIsDlssNr { get; init; }
    /// <summary>Where the installed OptiScaler came from (<see cref="AutoSetup.SourceKey"/>), when Hybrid Launcher installed it.</summary>
    public string? OptiSourceNow { get; init; }
    public DetectedReShade? ReShade { get; init; }
    /// <summary>The ReShade in the folder is the normal build (RenoDX and Luma need full add-on support).</summary>
    public bool ReShadeLimited { get; init; }
    public bool RenoDxInstalled { get; init; }
    public bool LumaInstalled { get; init; }
    /// <summary>DXVK or VKD3D-Proton is in: the game renders through Vulkan.</summary>
    public bool Layer { get; init; }
    /// <summary>DLSS 5 already in this game: "f5", "amd" (through OptiScaler) or "amd-reshade"; null = none.</summary>
    public string? Dlss5Now { get; init; }
    /// <summary>DLSSNR-AMD's own ReShade is in as dxgi.dll (OptiScaler cannot go in over it).</summary>
    public bool DlssNrAmdReShade => Dlss5Now == AutoSetup.AmdReShade;
    /// <summary>The OptiScaler wiki's "Filename" for the game (dxgi.dll, winmm.dll…), or null.</summary>
    public string? WikiFilename { get; init; }
    /// <summary>The OptiScaler wiki says the game needs no Nvidia spoofing (false), or nothing (null).</summary>
    public bool? WikiSpoofing { get; init; }
    /// <summary>OptiPatcher supports the game (true), does not (false), or its list could not be read (null).</summary>
    public bool? OptiPatcher { get; init; }
    /// <summary>The spoofing the user set on the OptiScaler tab, or null while it follows the game.</summary>
    public bool? SpoofingSet { get; init; }
    public AutoCandidate? RenoDx { get; init; }
    public AutoCandidate? Luma { get; init; }
    /// <summary>Ultra+ has a mod for this game.</summary>
    public bool UltraFits { get; init; }
    public bool UltraInstalled { get; init; }
    /// <summary>The mod lists (RenoDX, Luma, Ultra+) are still being read: a mod not found yet is not "not listed".</summary>
    public bool Reading { get; init; }

    bool Known => Apis.Count > 0;
    bool Has(string api) => Apis.Contains(api, StringComparer.OrdinalIgnoreCase);
    /// <summary>A game the OptiScaler upscalers can hook: DX11, DX12 or Vulkan (or not known: most games are).</summary>
    public bool ModernApi => !Known || Has("DX11") || Has("DX12") || Has("Vulkan");
    public bool VulkanOnly => Known && Has("Vulkan") && !Apis.Any(a => a.StartsWith("DX", StringComparison.OrdinalIgnoreCase) || a == "OpenGL");
    public bool Dx9Only => Known && Has("DX9") && !Has("DX10") && !Has("DX11") && !Has("DX12");
    public bool OpenGlOnly => Known && Has("OpenGL") && !Apis.Any(a => a.StartsWith("DX", StringComparison.OrdinalIgnoreCase));
    public bool Dx12Only => Known && Has("DX12") && !Has("DX11") && !Has("DX10");
    /// <summary>The game renders with Direct3D (DXVK or VKD3D-Proton can translate it), or the API is not known.</summary>
    public bool Direct3D => !Known || Apis.Any(a => a.StartsWith("DX", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Which mods the user ticked and which builds they picked; null for one = the recommendation.</summary>
public sealed class AutoChoice
{
    public bool? Opti { get; set; }
    /// <summary>OptiScaler's source (<see cref="AutoSetup.SourceKey"/>): "stable", "nightly", "fork:owner/name", "custom:name".</summary>
    public string? OptiSource { get; set; }
    /// <summary>OptiScaler's file name, when picked by hand.</summary>
    public string? OptiDll { get; set; }
    /// <summary>"off", "f5", "amd" (DLSSNR-AMD through OptiScaler) or "amd-reshade".</summary>
    public string? Dlss5 { get; set; }
    public bool? ReShade { get; set; }
    /// <summary>
    /// ReShade's build picked by hand (2.0.0-rc.13): true the build with full add-on support, false the normal one, null the
    /// planner's pick (the add-on build only when a ticked mod needs it).
    /// </summary>
    public bool? ReShadeAddon { get; set; }
    public bool? RenoDx { get; set; }
    public bool? Luma { get; set; }
    public bool? Ultra { get; set; }
    public bool? Dxvk { get; set; }

    public bool Custom => Opti is not null || OptiSource is not null || OptiDll is not null || Dlss5 is not null || ReShade is not null || ReShadeAddon is not null
                          || RenoDx is not null || Luma is not null || Ultra is not null || Dxvk is not null;
}

/// <summary>One row of the plan: a mod, what will happen to it and why, and the settings it goes in with.</summary>
public sealed class AutoItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public AutoState State { get; set; }
    /// <summary>Ticked: it is part of the one click.</summary>
    public bool Selected { get; set; }
    /// <summary>The planner ticks it by itself for this game.</summary>
    public bool Recommended { get; set; }
    /// <summary>Kept ticked (or kept off) by another pick: RenoDX keeps ReShade, DLSS 5 keeps OptiScaler.</summary>
    public bool Locked { get; set; }
    /// <summary>Already in, and can be put in again with the build picked here.</summary>
    public bool Replaceable { get; set; }
    /// <summary>Why it is recommended, not, or not possible, in one sentence.</summary>
    public string Why { get; set; } = "";
    /// <summary>The settings it goes in with, one line each.</summary>
    public List<string> Settings { get; } = [];

    public bool Acts => Selected && State is AutoState.Install or AutoState.Update;
    public bool CanTick => !Locked && (State is AutoState.Install or AutoState.Update || State == AutoState.Installed && Replaceable);
}

/// <summary>The whole plan: the rows, the conflicts it solved, and the names and builds each mod goes in with.</summary>
public sealed class AutoPlan
{
    public List<AutoItem> Items { get; } = [];
    /// <summary>Conflicts the plan avoids, in plain words ("ReShade and OptiScaler both want dxgi.dll: …").</summary>
    public List<string> Resolved { get; } = [];

    /// <summary>The DLSS 5 way the plan goes with ("off", "f5", "amd", "amd-reshade").</summary>
    public string Dlss5 { get; set; } = "off";
    /// <summary>OptiScaler's source (<see cref="AutoSetup.SourceKey"/>).</summary>
    public string OptiSource { get; set; } = AutoSetup.Stable;
    /// <summary>Why OptiScaler's source is fixed (DLSS 5 needs that build), or null when any allowed one can be picked.</summary>
    public string? OptiSourceWhy { get; set; }
    public string OptiDll { get; set; } = "dxgi.dll";
    /// <summary>The installed OptiScaler is renamed to this first (its name is one Ultra+ needs); null = stays.</summary>
    public string? OptiRename { get; set; }
    public bool Spoofing { get; set; } = true;
    /// <summary>The name ReShade's dll goes in as: ReShade64.dll when OptiScaler loads it, else the API's proxy name.</summary>
    public string ReShadeDll { get; set; } = "dxgi.dll";
    public bool ReShadeAddon { get; set; }
    /// <summary>A ticked or installed mod needs the add-on build (RenoDX): the build cannot be picked (2.0.0-rc.13).</summary>
    public bool ReShadeAddonNeeded { get; set; }
    /// <summary>OptiScaler loads ReShade (LoadReshade = true in OptiScaler.ini).</summary>
    public bool ReShadeThroughOpti { get; set; }
    /// <summary>A ReShade already in the folder under the name OptiScaler takes: renamed to ReShade64.dll before OptiScaler goes in.</summary>
    public string? MoveReShadeFirst { get; set; }

    public AutoItem? this[string id] => Items.FirstOrDefault(i => i.Id == id);
    public bool AnyToDo => Items.Any(i => i.Acts);

    /// <summary>The OptiScaler sources the DLSS 5 pick allows: F5 needs its own fork (or a build imported by hand); DLSSNR-AMD a build without DLSS NR.</summary>
    public bool AllowsSource(string key) => Dlss5 switch
    {
        AutoSetup.F5 => key == AutoSetup.F5Source || key.StartsWith("custom:", StringComparison.OrdinalIgnoreCase),
        AutoSetup.Amd => !AutoSetup.IsNrSource(key),
        _ => true,
    };
}

/// <summary>
/// The Auto tab's planner: from what a game is and holds and what the user picked, which mods go in, under which file names,
/// from which builds and with which settings, so that they work together. Pure logic (no files touched), so it can be tested.
/// Rules: OptiScaler only in 64-bit DX11/DX12/Vulkan games, under a proxy name nobody else holds (the OptiScaler wiki's first,
/// never one Ultra+ needs); DLSS 5's F5 needs janblade's F5 build of OptiScaler and DLSSNR-AMD one without DLSS NR (the nightly
/// by default), or no OptiScaler at all for its ReShade way; ReShade beside OptiScaler goes in as ReShade64.dll and OptiScaler
/// loads it; RenoDX needs ReShade with full add-on support; RenoDX and Luma never together (both rewrite the image over
/// ReShade, and Luma brings its own); a generic RenoDX or Luma mod (one made for an engine) is never recommended;
/// DXVK/VKD3D-Proton go with any of them (2.0.0-rc.16: whether a mod works through a Vulkan layer is the user's call); the
/// others then take proxy names the layer does not use.
/// </summary>
public static class AutoSetup
{
    public const string Stable = "stable", Nightly = "nightly";
    public const string Off = "off", F5 = Dlss5.F5, Amd = Dlss5.Amd, AmdReShade = "amd-reshade";
    public static readonly string F5Source = "fork:" + Dlss5.F5Repo;

    /// <summary>OptiScaler's names in the order tried when the wiki gives none or its name is taken.</summary>
    static readonly string[] OptiNames = ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll"];

    /// <summary>The names Ultra+ loads UE4SS through beside the exe (dwmapi.dll for most games).</summary>
    public static readonly string[] UltraNames = ["dwmapi.dll", "version.dll", "winmm.dll", "xinput1_3.dll", "xinput1_4.dll"];

    /// <summary>The key of an OptiScaler source: "stable", "nightly", "fork:owner/name", "custom:name".</summary>
    public static string SourceKey(OptiChannel channel, string? repoOrName) => channel switch
    {
        OptiChannel.Nightly => Nightly,
        OptiChannel.Fork => "fork:" + repoOrName,
        OptiChannel.Custom => "custom:" + repoOrName,
        _ => Stable,
    };

    /// <summary>A source whose builds carry DLSS NR (F5 and the other NR forks): DLSSNR-AMD does not go into them.</summary>
    public static bool IsNrSource(string key) =>
        key.Equals(F5Source, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("fork:", StringComparison.OrdinalIgnoreCase) && (key.Contains("dlssnr", StringComparison.OrdinalIgnoreCase) || key.Contains("-NR-", StringComparison.OrdinalIgnoreCase));

    /// <summary>The DLSS 5 ways this card can run, the recommended one first.</summary>
    public static List<string> Dlss5Ways(Dlss5Gpu tier) =>
        tier is Dlss5Gpu.AmdRdna4 or Dlss5Gpu.AmdRdna3 ? [Amd, AmdReShade, F5] : [F5];

    public static string Dlss5Name(string way) => way switch
    {
        F5 => L.T("F5 Multipass (janblade's OptiScaler build)"),
        Amd => L.T("DLSSNR-AMD through OptiScaler"),
        AmdReShade => L.T("DLSSNR-AMD through ReShade (no OptiScaler)"),
        _ => L.T("Off"),
    };

    public static AutoPlan Plan(AutoFacts f, AutoChoice? choice = null)
    {
        choice ??= new AutoChoice();
        var plan = new AutoPlan();
        var opti = new AutoItem { Id = "opti", Name = "OptiScaler" };
        var dlss5 = new AutoItem { Id = "dlss5", Name = "DLSS 5" };
        var reshade = new AutoItem { Id = "reshade", Name = "ReShade" };
        var renodx = new AutoItem { Id = "renodx", Name = "RenoDX" };
        var luma = new AutoItem { Id = "luma", Name = "Luma" };
        var ultra = new AutoItem { Id = "ultra", Name = "Ultra+" };
        var dxvk = new AutoItem { Id = "dxvk", Name = "DXVK / VKD3D-Proton" };
        // DLSS 5 first: its way decides which OptiScaler build fits.
        plan.Items.AddRange([dlss5, opti, reshade, renodx, luma, ultra, dxvk]);

        // ---------- DXVK / VKD3D-Proton: the game's Direct3D through Vulkan ----------
        bool optiIn = f.OptiFile is not null;
        if (f.Layer) { dxvk.State = AutoState.Installed; dxvk.Why = L.T("A Vulkan layer is already in this game. It is managed in Mods → DXVK."); }
        else if (!f.Direct3D) { dxvk.State = AutoState.NotPossible; dxvk.Why = L.T("The game does not render with Direct3D: there is nothing to translate."); }
        else
        {
            dxvk.State = AutoState.Install;
            dxvk.Why = L.T("Direct3D through Vulkan: helps older games that stutter on AMD and Intel. Some mods may not work through it: try and see.");
            dxvk.Selected = choice.Dxvk == true;
            dxvk.Settings.Add(f.Dx12Only ? L.T("VKD3D-Proton with DXVK's dxgi.dll, the release picked") : L.T("DXVK for the game's Direct3D version, the release picked"));
            dxvk.Settings.Add(L.T("Every file it replaces is backed up"));
        }
        // The layer's own dlls are not free for the others' proxy names.
        if (dxvk.Acts)
            f = f.WithTaken(f.Dx12Only ? ["d3d12.dll", "d3d12core.dll", "dxgi.dll"] : f.Dx9Only ? ["d3d9.dll"] : ["d3d11.dll", "dxgi.dll", "d3d10core.dll"], "layer");

        // ---------- DLSS 5: decides which OptiScaler build fits ----------
        var ways = Dlss5Ways(f.Dlss5Tier);
        if (f.Dlss5Now is { } now)
        {
            dlss5.State = AutoState.Installed;
            plan.Dlss5 = now;
            dlss5.Why = L.F("{0} is in this game. Its settings and networks are in Mods → DLSS 5.", Dlss5Name(now));
        }
        else if (!f.Is64 || !f.ModernApi)
        {
            dlss5.State = AutoState.NotPossible;
            dlss5.Why = L.T("DLSS 5 needs a 64-bit DirectX 11, DirectX 12 or Vulkan game.");
        }
        else
        {
            dlss5.State = AutoState.Install;
            var way = choice.Dlss5 is { } w && (w == Off || ways.Contains(w)) ? w : Off;
            if (way == AmdReShade && optiIn) way = Amd;
            plan.Dlss5 = way;
            dlss5.Selected = way != Off;
            dlss5.Why = L.T("Neural rendering. Tick it and select a method; it needs NVIDIA's nvngx_dlssnr.dll once (Hybrid Launcher asks for it and keeps it).");
            if (dlss5.Selected) dlss5.Settings.Add(Dlss5Name(way));
            if (way == F5) dlss5.Settings.Add(f.Dlss5Tier == Dlss5Gpu.NvidiaRtx ? L.T("NVIDIA's NGX runs it") : L.T("The vendor-neutral port runs it on this card"));
            if (way == Amd) dlss5.Settings.Add(L.T("Laid over OptiScaler as its plugin; End opens NR Control in game"));
            if (way == AmdReShade) dlss5.Settings.Add(L.T("Brings its own ReShade with the NR add-on; Home opens it in game"));
        }

        // ---------- OptiScaler ----------
        bool ultraWanted = f.UltraInstalled || f.UltraFits && choice.Ultra == true;
        if (!f.Is64) Not(opti, L.T("The game is 32-bit; OptiScaler works only in 64-bit games."));
        else if (!f.ModernApi) Not(opti, L.F("The game renders with {0}; OptiScaler needs DirectX 11, DirectX 12 or Vulkan.", string.Join(" / ", f.Apis)));
        else if (plan.Dlss5 == AmdReShade)
        {
            if (optiIn) opti.State = AutoState.Installed;
            else Not(opti, L.T("Left out: DLSSNR-AMD's ReShade method goes without OptiScaler."));
            opti.Why = optiIn ? L.F("Already in this game as {0}.", f.OptiFile!) : opti.Why;
        }
        else if (optiIn)
        {
            opti.State = AutoState.Installed;
            opti.Replaceable = true;
            plan.OptiDll = f.OptiFile!;
            opti.Why = L.F("Already in this game as {0}. Tick it to put the build picked below in its place.", f.OptiFile!);
            // DLSS 5 needs another build than the one in: it is replaced.
            if (plan.Dlss5 == F5 && dlss5.Acts && !f.OptiIsDlssNr || plan.Dlss5 == Amd && dlss5.Acts && f.OptiIsDlssNr)
            {
                opti.Selected = true;
                opti.Locked = true;
                opti.Why = plan.Dlss5 == F5
                    ? L.T("The build in is not F5: DLSS 5's F5 method replaces it with janblade's F5 build.")
                    : L.T("The build in is a DLSS NR one, which DLSSNR-AMD does not go into: it is replaced by an official build.");
            }
            else opti.Selected = choice.Opti == true;
            if (opti.Selected) opti.State = AutoState.Update;
        }
        else if (f.DlssNrAmdReShade) Not(opti, L.T("DLSSNR-AMD is in through its own ReShade. Take it out first (Mods → DLSS 5)."));
        else
        {
            opti.State = AutoState.Install;
            opti.Recommended = true;
            opti.Why = L.T("Upscaling (FSR, XeSS, DLSS) and frame generation for any card, with its in-game menu on Insert.");
            bool needed = plan.Dlss5 is F5 or Amd && dlss5.Acts;
            opti.Locked = needed;
            opti.Selected = needed || (choice.Opti ?? true);
            if (needed) opti.Why = L.T("DLSS 5's method goes through OptiScaler.");
        }

        if (opti.Acts)
        {
            // The build: DLSS 5 fixes the source for F5; DLSSNR-AMD wants one that loads plugins (the nightly by default).
            string recommended = plan.Dlss5 == F5 && dlss5.Acts ? F5Source : plan.Dlss5 == Amd && dlss5.Acts ? Nightly
                // A build in that is put in again keeps its own source unless DLSS 5 needs another.
                : opti.State == AutoState.Update && f.OptiSourceNow is { } nowSource && plan.AllowsSource(nowSource) ? nowSource : Stable;
            plan.OptiSource = choice.OptiSource is { } src && plan.AllowsSource(src) ? src : recommended;
            if (plan.Dlss5 == F5 && dlss5.Acts) plan.OptiSourceWhy = L.T("DLSS 5's F5 method needs janblade's F5 build (or one you imported).");
            else if (plan.Dlss5 == Amd && dlss5.Acts) plan.OptiSourceWhy = L.T("DLSSNR-AMD goes into builds without DLSS NR that load plugins: the nightly is picked first.");
            if (choice.OptiSource is { } asked && asked != plan.OptiSource)
                plan.Resolved.Add(L.T("The OptiScaler build picked does not fit the DLSS 5 method: the one it needs is picked instead."));

            if (opti.State == AutoState.Install)
            {
                var name = choice.OptiDll is { } d && NameFree(f, d, ultraWanted) ? d.ToLowerInvariant() : FreeOptiName(f, ultraWanted);
                if (name is null) { Not(opti, L.T("Every file name OptiScaler can load as is taken by another mod in this folder.")); }
                else
                {
                    plan.OptiDll = name;
                    if (choice.OptiDll is { } d2 && !d2.Equals(name, StringComparison.OrdinalIgnoreCase))
                        plan.Resolved.Add(L.F("{0} is taken in this folder: OptiScaler goes in as {1}.", d2, name));
                    if (f.WikiFilename is { } w && !w.Equals(name, StringComparison.OrdinalIgnoreCase) && choice.OptiDll is null && f.Files.ContainsKey(w))
                        plan.Resolved.Add(L.F("The OptiScaler wiki names {0} for this game, but another mod holds it: OptiScaler goes in as {1}.", w, name));
                }
            }
        }
        if (opti.Acts)
        {
            opti.Settings.Add(L.F("Build: {0}", SourceName(plan.OptiSource)));
            opti.Settings.Add(f.WikiFilename is { } wiki && wiki.Equals(plan.OptiDll, StringComparison.OrdinalIgnoreCase)
                ? L.F("As {0}, the name the OptiScaler wiki gives for this game", plan.OptiDll)
                : L.F("As {0}", plan.OptiDll));
            if (f.Nvidia) opti.Settings.Add(L.T("Nvidia card: DLSS inputs work without spoofing"));
            else
            {
                // Smart default (2.0.0-rc.11): the user's own choice first, then off for a game OptiPatcher supports (it
                // unlocks the DLSS inputs without spoofing), then the wiki, else on.
                plan.Spoofing = f.SpoofingSet ?? (f.OptiPatcher == true ? false : f.WikiSpoofing ?? true);
                opti.Settings.Add(f.SpoofingSet is { } own
                    ? own ? L.T("Nvidia spoofing on, as set on the OptiScaler tab") : L.T("Nvidia spoofing off, as set on the OptiScaler tab")
                    : f.OptiPatcher == true ? L.T("Spoofing off: OptiPatcher supports this game and unlocks the DLSS inputs without it")
                    : plan.Spoofing ? L.T("AMD / Intel card: Nvidia spoofing on, so the game offers DLSS for OptiScaler to replace")
                    : L.T("Spoofing off: the OptiScaler wiki says this game does not need it"));
                opti.Settings.Add(f.OptiPatcher switch
                {
                    true => L.T("OptiPatcher: this game is on its list"),
                    false => L.T("OptiPatcher: this game is not on its list"),
                    _ => L.T("OptiPatcher when the game is on its list"),
                });
            }
        }
        // OptiScaler in under a name Ultra+ needs: renamed in place (nothing reinstalled).
        if (optiIn && !opti.Acts && f.UltraFits && !f.UltraInstalled && choice.Ultra == true && UltraNames.Contains(f.OptiFile!, StringComparer.OrdinalIgnoreCase))
        {
            plan.OptiRename = FreeOptiName(f, true);
            if (plan.OptiRename is not null)
                plan.Resolved.Add(L.F("OptiScaler is {0}, a name Ultra+ loads through: it is renamed to {1} first (nothing reinstalled).", f.OptiFile!, plan.OptiRename));
        }
        bool optiThere = opti.State == AutoState.Installed || opti.Acts;

        // ---------- RenoDX and Luma: one at a time (2.0.0-rc.18: RenoDX is mainly for HDR, Luma is not only HDR) ----------
        bool amdReShade = plan.Dlss5 == AmdReShade && (dlss5.Acts || f.DlssNrAmdReShade);
        string hdrWhy = f.Hdr ? L.T("Mainly an HDR mod (some also fix other things), and a display of this PC has HDR on.")
            : L.T("Mainly an HDR mod (some also fix other things): tick it if you play in HDR (no display of this PC has HDR on now).");
        if (f.RenoDxInstalled) { renodx.State = AutoState.Installed; renodx.Replaceable = !amdReShade; renodx.Why = L.T("A RenoDX mod is already in this game. Tick it to download its newest snapshot again."); }
        else if (f.RenoDx is null) Not(renodx, f.Reading ? L.T("Reading its list…") : L.T("No RenoDX mod is listed for this game."));
        else if (!f.RenoDx.HasDownload) Not(renodx, L.F("{0} has no direct download (Nexus Mods only): see Mods → RenoDX.", f.RenoDx.Name));
        else if (amdReShade) Not(renodx, L.T("Left out: DLSSNR-AMD's ReShade sets other add-ons aside."));
        else
        {
            renodx.State = AutoState.Install;
            // A generic mod (one for every game of an engine) is offered, never ticked by itself: it may not work in this game.
            renodx.Recommended = f.Hdr && f.RenoDx.Working && !f.RenoDx.Generic && !f.LumaInstalled;
            renodx.Why = GenericWhy(f.RenoDx) + (f.RenoDx.Working ? "" : L.T("Listed as in progress.") + " ") + hdrWhy;
        }
        if (renodx.State is AutoState.Install or AutoState.Installed && f.RenoDx is not null)
        {
            renodx.Settings.Add(L.F("{0}, the newest snapshot ({1}-bit)", f.RenoDx.Name, f.Is64 ? "64" : "32"));
            renodx.Settings.Add(L.T("Its settings are on ReShade's Add-ons page (Home in game)"));
        }

        if (f.LumaInstalled) { luma.State = AutoState.Installed; luma.Replaceable = !amdReShade; luma.Why = L.T("A Luma mod is already in this game. Tick it to put the build picked below in its place."); }
        else if (f.Luma is null) Not(luma, f.Reading ? L.T("Reading its list…") : L.T("No Luma mod is listed for this game."));
        else if (!f.Luma.HasDownload) Not(luma, L.F("{0} has no build on GitHub yet: see Mods → Luma.", f.Luma.Name));
        else if (amdReShade) Not(luma, L.T("Left out: DLSSNR-AMD brings its own ReShade, and Luma brings another."));
        else
        {
            luma.State = AutoState.Install;
            luma.Recommended = f.Hdr && f.Luma.Working && !f.Luma.Generic && !(renodx.Recommended || f.RenoDxInstalled);
            luma.Why = GenericWhy(f.Luma) + (f.Luma.Working ? "" : L.T("Listed as in progress.") + " ")
                + L.T("Reworks the game's rendering; what it adds depends on the mod (upscaling, HDR, fixes). Mods → Luma lists what this one does.");
        }
        if (luma.State is AutoState.Install or AutoState.Installed && f.Luma is not null)
        {
            luma.Settings.Add(L.F("{0}, the build picked below", f.Luma.Name));
            luma.Settings.Add(L.T("Brings its own ReShade with full add-on support"));
            if (optiThere) luma.Settings.Add(L.T("OptiScaler loads Luma's ReShade (ReShade64.dll)"));
        }
        renodx.Selected = renodx.CanTick && (renodx.State == AutoState.Installed ? choice.RenoDx == true : choice.RenoDx ?? renodx.Recommended);
        luma.Selected = luma.CanTick && (luma.State == AutoState.Installed ? choice.Luma == true : choice.Luma ?? luma.Recommended);
        // Both rewrite the image over the same ReShade: the one ticked last wins in the page (it clears the other); here RenoDX.
        if (renodx.Selected && luma.Selected)
        {
            if (choice.Luma == true) plan.Resolved.Add(L.T("RenoDX and Luma both rework the image through ReShade: only one goes in. RenoDX is kept."));
            luma.Selected = false;
        }
        if (renodx.Selected && f.LumaInstalled && !f.RenoDxInstalled)
        {
            renodx.Selected = false;
            plan.Resolved.Add(L.T("A Luma mod is in: RenoDX is left out (they do not go together)."));
        }
        if (luma.Selected && f.RenoDxInstalled && !f.LumaInstalled)
        {
            luma.Selected = false;
            plan.Resolved.Add(L.T("A RenoDX mod is already in: Luma is left out (they do not go together)."));
        }
        if (luma.State == AutoState.Installed && luma.Selected) luma.State = AutoState.Update;
        if (renodx.State == AutoState.Installed && renodx.Selected) renodx.State = AutoState.Update;
        if (renodx.Selected && luma.State == AutoState.Install && !luma.Selected) luma.Why = L.T("Left out: RenoDX is ticked, and RenoDX and Luma do not go together.");
        if (luma.Selected && renodx.State == AutoState.Install && !renodx.Selected) renodx.Why = L.T("Left out: Luma is ticked, and RenoDX and Luma do not go together.");

        // ---------- ReShade ----------
        bool needsAddon = renodx.Selected || f.RenoDxInstalled;
        if (f.VulkanOnly)
            Not(reshade, L.T("The game renders through Vulkan, where ReShade goes in as a layer: use ReShade's own installer (Mods → ReShade)."));
        else if (amdReShade) { reshade.State = AutoState.Covered; reshade.Why = L.T("DLSSNR-AMD brings its own ReShade with the NR add-on."); }
        else if (luma.Selected || f.LumaInstalled) { reshade.State = AutoState.Covered; reshade.Why = L.T("Luma brings its own ReShade with full add-on support."); }
        else if (f.ReShade is { } rs)
        {
            reshade.State = AutoState.Installed;
            reshade.Replaceable = true;
            reshade.Why = L.F("ReShade {0} is already in as {1}. Tick it to put the version picked below in its place.", rs.Version, rs.FileName);
            if (needsAddon && f.ReShadeLimited)
            {
                reshade.Why = L.F("ReShade {0} is the normal build, which skips RenoDX: it is replaced by the build with full add-on support.", rs.Version);
                reshade.Selected = true;
                reshade.Locked = renodx.Selected;
            }
            else reshade.Selected = choice.ReShade == true;
            if (reshade.Selected) reshade.State = AutoState.Update;
        }
        else
        {
            reshade.State = AutoState.Install;
            reshade.Recommended = renodx.Selected;
            reshade.Locked = renodx.Selected;
            reshade.Why = renodx.Selected ? L.T("RenoDX runs inside ReShade.") : L.T("Post-processing filters (sharpening, colour, SMAA). Tick it if you want them.");
            reshade.Selected = reshade.Locked || (choice.ReShade ?? reshade.Recommended);
        }

        if (reshade.Acts)
        {
            plan.ReShadeAddonNeeded = needsAddon;
            plan.ReShadeAddon = needsAddon || choice.ReShadeAddon == true;
            if (optiThere && plan.Dlss5 != AmdReShade)
            {
                plan.ReShadeDll = ReShade.OptiDllName;
                plan.ReShadeThroughOpti = true;
                plan.Resolved.Add(L.T("ReShade and OptiScaler would both be dxgi.dll: ReShade goes in as ReShade64.dll and OptiScaler loads it."));
            }
            else if (f.ReShade is { LoadedByOpti: false } keep) plan.ReShadeDll = keep.FileName;
            else if (ReShadeName(f) is { } rsName) plan.ReShadeDll = rsName;
            else Not(reshade, L.F("{0} is taken by another mod in this folder.", f.Dx9Only ? "d3d9.dll" : "dxgi.dll"));
            if (reshade.Acts)
            {
                reshade.Settings.Add(plan.ReShadeAddonNeeded ? L.T("The build with full add-on support (RenoDX needs it)")
                    : plan.ReShadeAddon ? L.T("The build with full add-on support (avoid it in online games with anti-cheat)") : L.T("The normal build (safer in online games)"));
                reshade.Settings.Add(plan.ReShadeThroughOpti ? L.T("As ReShade64.dll, loaded by OptiScaler") : L.F("As {0}", plan.ReShadeDll));
                reshade.Settings.Add(L.T("Standard effects (SMAA, LumaSharpen, Vibrance and more) in reshade-shaders"));
                reshade.Settings.Add(L.T("Performance mode on, the tutorial skipped, overlay on Home"));
            }
        }
        // A ReShade that is already a proxy dll where OptiScaler goes: OptiScaler takes the name, ReShade becomes ReShade64.dll.
        if (opti.Acts && f.ReShade is { LoadedByOpti: false } old && old.FileName.Equals(plan.OptiDll, StringComparison.OrdinalIgnoreCase))
        {
            plan.MoveReShadeFirst = old.FileName;
            plan.Resolved.Add(L.F("ReShade is {0}, the name OptiScaler takes: it becomes ReShade64.dll and OptiScaler loads it.", old.FileName));
        }
        else if (opti.Acts && opti.State == AutoState.Install && f.ReShade is { LoadedByOpti: false } other && !reshade.Acts)
            plan.Resolved.Add(L.F("ReShade stays {0}; OptiScaler goes in as {1} beside it.", other.FileName, plan.OptiDll));

        // ---------- Ultra+ ----------
        if (f.UltraInstalled)
        {
            ultra.State = AutoState.Installed;
            ultra.Replaceable = true;
            ultra.Why = L.T("Ultra+ is in this game. Tick it to put the build picked below in its place; add-ons and settings are in the Ultra+ tab.");
            ultra.Selected = choice.Ultra == true;
            if (ultra.Selected) ultra.State = AutoState.Update;
        }
        else if (!f.UltraFits) Not(ultra, f.Reading ? L.T("Reading its list…") : L.T("Ultra+ has no mod for this game."));
        else
        {
            ultra.State = AutoState.Install;
            ultra.Why = L.T("Graphics mods for this Unreal Engine game (ray tracing, fixes), loaded through UE4SS. Add-ons and settings are in the Ultra+ tab afterwards.");
            ultra.Selected = choice.Ultra == true;
        }
        if (ultra.Acts)
        {
            ultra.Settings.Add(L.T("The build picked below, with its UE4SS"));
            if (opti.Acts || optiIn) ultra.Settings.Add(L.T("OptiScaler keeps a name Ultra+ does not load through"));
        }

        return plan;
    }

    /// <summary>The warning a generic mod's row starts with (2.0.0-rc.18), or nothing for a mod made for the game.</summary>
    static string GenericWhy(AutoCandidate c) => c.Engine is null ? ""
        : c.Tested ? L.F("Generic mod for {0} games, not made for this one (its list says it was tried with it).", EngineName(c.Engine)) + " "
        : L.F("Generic mod for {0} games, not made for this one: it may not work here or may not be needed.", EngineName(c.Engine)) + " ";

    /// <summary>"Unreal Engine", "Unity", "Source Engine" from the lists' short engine names.</summary>
    public static string EngineName(string engine) => engine switch
    {
        "Unreal" => "Unreal Engine",
        "Source" => "Source Engine",
        _ => engine,
    };

    static void Not(AutoItem item, string why)
    {
        item.State = AutoState.NotPossible;
        item.Selected = false;
        item.Locked = false;
        item.Why = why;
        item.Settings.Clear();
    }

    /// <summary>"Stable", "Nightly", the fork's or the imported build's name.</summary>
    public static string SourceName(string key) =>
        key == Stable ? L.T("Stable") : key == Nightly ? L.T("Nightly")
        : key.StartsWith("fork:", StringComparison.OrdinalIgnoreCase) ? (OptiForks.Known.FirstOrDefault(k => ("fork:" + k.Repo).Equals(key, StringComparison.OrdinalIgnoreCase))?.Name ?? key[5..])
        : key.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) ? L.F("Imported: {0}", key[7..]) : key;

    static bool NameFree(AutoFacts f, string name, bool ultra) =>
        OptiNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        && !(ultra && UltraNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        && (!f.Files.TryGetValue(name, out var what) || what is "reshade" or "opti");

    /// <summary>The first proxy name OptiScaler can take: the wiki's, then the usual ones; free, or held by ReShade (it moves).</summary>
    static string? FreeOptiName(AutoFacts f, bool ultra)
    {
        var order = new List<string>();
        if (f.WikiFilename is { } w && OptiSetupOptions.DllNames.Contains(w, StringComparer.OrdinalIgnoreCase) && !w.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
            order.Add(w.ToLowerInvariant());
        // dxgi.dll is not loaded by a Vulkan game.
        if (f.VulkanOnly) order.Add("winmm.dll");
        order.AddRange(OptiNames);
        foreach (var name in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (f.VulkanOnly && name == "dxgi.dll") continue;
            if (NameFree(f, name, ultra)) return name;
        }
        return null;
    }

    /// <summary>The names OptiScaler can be given by hand in this game.</summary>
    public static IEnumerable<string> OptiNameChoices(AutoFacts f, bool ultra) =>
        OptiNames.Where(n => !(f.VulkanOnly && n == "dxgi.dll") && NameFree(f, n, ultra));

    /// <summary>ReShade's proxy name without OptiScaler: the game's API decides, and a name another mod holds is skipped.</summary>
    static string? ReShadeName(AutoFacts f)
    {
        string[] order = f.Dx9Only ? ["d3d9.dll"] : f.OpenGlOnly ? ["opengl32.dll"] : f.Dx12Only ? ["dxgi.dll", "d3d12.dll"] : ["dxgi.dll", "d3d11.dll"];
        return order.FirstOrDefault(n => !f.Files.TryGetValue(n, out var what) || what == "reshade");
    }

    /// <summary>Proxy dll names that matter to the plan.</summary>
    static readonly string[] Proxies = ["dxgi.dll", "d3d9.dll", "d3d11.dll", "d3d12.dll", "opengl32.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll", "dinput8.dll", "dwmapi.dll"];

    /// <summary>The proxy dlls in the game folder and whose they are (see <see cref="AutoFacts.Files"/>).</summary>
    public static Dictionary<string, string> ReadFolder(string dir, GameSettings settings)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Proxies)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            files[name] = OptiDetector.IsOptiScaler(path) ? "opti"
                : ReShade.IsReShade(path) ? "reshade"
                : Dxvk.LayerOf(path) is not null ? "layer"
                : settings.UltraPlus is { } u && UltraPlus.Owns(u, path) ? "ultra"
                : "other";
        }
        return files;
    }

    /// <summary>"Dxgi = false", "spoofing is unnecessary"…: what the OptiScaler wiki's Settings text says about spoofing.</summary>
    public static bool? SpoofingFromWiki(string? settingsText)
    {
        if (string.IsNullOrWhiteSpace(settingsText)) return null;
        var t = settingsText.ToLowerInvariant();
        if (t.Contains("dxgi = false") || t.Contains("dxgi=false") || t.Contains("spoofing is unnecessary") || t.Contains("spoofing unnecessary")
            || t.Contains("no spoofing") || t.Contains("spoofing not needed") || t.Contains("disable spoofing"))
            return false;
        return null;
    }
}
