using HybridLauncher.Core;

namespace HybridLauncher;

/// <summary>What a controller button can do in Hybrid Launcher (the D-pad and left stick always move, the right stick scrolls).</summary>
public enum PadAction { None, Press, Back, Menu, Play, PrevTab, NextTab, PageUp, PageDown, GameList, Search, PrevSection, NextSection }

/// <summary>
/// Which button does what (Settings → Controller): the defaults, as on consoles, and the ones changed there
/// (<see cref="ControllerSettings.Buttons"/>). A button does one thing: giving it to an action takes it from the one that had it,
/// which gets the button the first had instead.
/// </summary>
public static class PadActions
{
    /// <summary>The buttons that can be given an action, Xbox names (A is Cross on a PlayStation controller, and so on).</summary>
    public static readonly PadKey[] Buttons = [PadKey.A, PadKey.B, PadKey.X, PadKey.Y, PadKey.LB, PadKey.RB, PadKey.LT, PadKey.RT, PadKey.View, PadKey.Menu, PadKey.L3, PadKey.R3];

    /// <summary>The actions in the order Settings lists them, with their name and what they do.</summary>
    public static readonly (PadAction Action, string Name, string Description)[] All =
    [
        (PadAction.Press, "Press", "Presses what has the focus: a button, a game, a switch."),
        (PadAction.Back, "Back", "Closes a menu or dialog, or goes back a page."),
        (PadAction.Menu, "Options menu", "The right-click menu of what has the focus (a game in the library, a list item)."),
        (PadAction.Play, "Play", "Starts the game of the open page."),
        (PadAction.PrevTab, "Previous tab", "The tab on the left: on a game page, in Settings; on pages without tabs, the previous section."),
        (PadAction.NextTab, "Next tab", "The tab on the right: on a game page, in Settings; on pages without tabs, the next section."),
        (PadAction.PageUp, "Page up", "Scrolls up a whole page."),
        (PadAction.PageDown, "Page down", "Scrolls down a whole page."),
        (PadAction.GameList, "Game list", "Jumps to the list of games on the left."),
        (PadAction.Search, "Search", "Puts the cursor in Search games."),
        (PadAction.PrevSection, "Previous section", "My PC, Library, Downloads and Settings, one after another, from any page."),
        (PadAction.NextSection, "Next section", "The same, the other way."),
    ];

    static readonly Dictionary<PadAction, PadKey> Defaults = new()
    {
        [PadAction.Press] = PadKey.A,
        [PadAction.Back] = PadKey.B,
        [PadAction.Menu] = PadKey.X,
        [PadAction.Play] = PadKey.Y,
        [PadAction.PrevTab] = PadKey.LB,
        [PadAction.NextTab] = PadKey.RB,
        [PadAction.PageUp] = PadKey.LT,
        [PadAction.PageDown] = PadKey.RT,
        [PadAction.GameList] = PadKey.Menu,
        [PadAction.Search] = PadKey.View,
    };

    static Dictionary<string, string> Saved => AppState.Settings.Controller.Buttons;

    /// <summary>The button of <paramref name="action"/>, or null when it has none.</summary>
    public static PadKey? ButtonOf(PadAction action)
    {
        if (Saved.TryGetValue(action.ToString(), out var b))
            return Enum.TryParse<PadKey>(b, out var k) && Buttons.Contains(k) ? k : null;
        return Defaults.TryGetValue(action, out var d) ? d : null;
    }

    /// <summary>What <paramref name="button"/> does, or <see cref="PadAction.None"/>.</summary>
    public static PadAction ActionOf(PadKey button)
    {
        foreach (var (action, _, _) in All)
            if (ButtonOf(action) == button) return action;
        return PadAction.None;
    }

    /// <summary>Gives <paramref name="button"/> (null: none) to <paramref name="action"/>; the action that had it gets the old one.</summary>
    public static void Set(PadAction action, PadKey? button)
    {
        var old = ButtonOf(action);
        if (button is { } b)
            foreach (var (other, _, _) in All)
                if (other != action && ButtonOf(other) == b) Store(other, old);
        Store(action, button);
    }

    static void Store(PadAction action, PadKey? button)
    {
        Defaults.TryGetValue(action, out var d);
        bool isDefault = button is { } b ? Defaults.ContainsKey(action) && d == b : !Defaults.ContainsKey(action);
        if (isDefault) Saved.Remove(action.ToString());
        else Saved[action.ToString()] = button?.ToString() ?? "None";
    }

    public static bool Changed => Saved.Count > 0;

    public static void Reset() => Saved.Clear();

    /// <summary>The button's name as printed on Xbox controllers, with the PlayStation one beside it.</summary>
    public static string Label(PadKey k) => k switch
    {
        PadKey.A => "A  (Cross)",
        PadKey.B => "B  (Circle)",
        PadKey.X => "X  (Square)",
        PadKey.Y => "Y  (Triangle)",
        PadKey.LB => "LB  (L1)",
        PadKey.RB => "RB  (R1)",
        PadKey.LT => "LT  (L2)",
        PadKey.RT => "RT  (R2)",
        PadKey.View => "View  (Create / Share)",
        PadKey.Menu => "Menu  (Options)",
        PadKey.L3 => "Left stick press  (L3)",
        PadKey.R3 => "Right stick press  (R3)",
        _ => k.ToString(),
    };
}
