using HybridLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;

namespace HybridLauncher.Helpers;

/// <summary>
/// Puts the text written in XAML into the UI language: called once on a page or control right after InitializeComponent,
/// it walks the element tree (flyouts and tooltips too) and swaps every text it has a translation for. Text set from code
/// goes through <see cref="L"/> directly.
/// </summary>
public static class Loc
{
    public static void Apply(object? node)
    {
        if (!L.Italian || node is null) return;
        Walk(node, 0);
    }

    static string? Tr(string? s) => string.IsNullOrEmpty(s) ? s : L.T(s);

    static void Walk(object? node, int depth)
    {
        if (node is null || depth > 64) return;
        if (node is string) return;

        if (node is DependencyObject d && ToolTipService.GetToolTip(d) is string tip) ToolTipService.SetToolTip(d, Tr(tip));
        if (node is UIElement ui && ui.ContextFlyout is { } context) Walk(context, depth + 1);
        if (node is FrameworkElement fe && FlyoutBase.GetAttachedFlyout(fe) is { } attached) Walk(attached, depth + 1);

        switch (node)
        {
            case TextBlock tb:
                if (tb.Inlines.Count > 0) foreach (var inline in tb.Inlines) Walk(inline, depth + 1);
                else tb.Text = Tr(tb.Text) ?? "";
                return;
            case Run run:
                run.Text = Tr(run.Text) ?? "";
                return;
            case Span span:
                foreach (var inline in span.Inlines) Walk(inline, depth + 1);
                return;
            case TextBox box:
                box.PlaceholderText = Tr(box.PlaceholderText) ?? "";
                if (box.Header is string th) box.Header = Tr(th);
                return;
            case AutoSuggestBox asb:
                asb.PlaceholderText = Tr(asb.PlaceholderText) ?? "";
                return;
            case ToggleSwitch ts:
                if (ts.OnContent is string on) ts.OnContent = Tr(on);
                if (ts.OffContent is string off) ts.OffContent = Tr(off);
                if (ts.Header is string h) ts.Header = Tr(h);
                return;
            case ComboBox combo:
                combo.PlaceholderText = Tr(combo.PlaceholderText) ?? "";
                if (combo.Header is string ch) combo.Header = Tr(ch);
                // Plain string items are values the code compares: only ComboBoxItems are translated.
                foreach (var item in combo.Items) if (item is ComboBoxItem) Walk(item, depth + 1);
                return;
            case SelectorBar bar:
                foreach (var item in bar.Items) Walk(item, depth + 1);
                return;
            case SelectorBarItem sbi:
                sbi.Text = Tr(sbi.Text) ?? "";
                return;
            case InfoBar info:
                info.Title = Tr(info.Title) ?? "";
                info.Message = Tr(info.Message) ?? "";
                Walk(info.ActionButton, depth + 1);
                return;
            case NavigationView nav:
                foreach (var item in nav.MenuItems) Walk(item, depth + 1);
                foreach (var item in nav.FooterMenuItems) Walk(item, depth + 1);
                Walk(nav.AutoSuggestBox, depth + 1);
                Walk(nav.PaneCustomContent, depth + 1);
                Walk(nav.PaneFooter, depth + 1);
                Walk(nav.Content, depth + 1);
                return;
            case NavigationViewItemHeader header:
                if (header.Content is string hs) header.Content = Tr(hs); else Walk(header.Content, depth + 1);
                return;
            case Expander expander:
                if (expander.Header is string eh) expander.Header = Tr(eh); else Walk(expander.Header, depth + 1);
                Walk(expander.Content, depth + 1);
                return;
            case ContentControl cc:
                if (cc.Content is string cs) cc.Content = Tr(cs); else Walk(cc.Content, depth + 1);
                if (cc is Button { Flyout: { } bf }) Walk(bf, depth + 1);
                return;
            case MenuFlyout menu:
                foreach (var item in menu.Items) Walk(item, depth + 1);
                return;
            case MenuFlyoutSubItem sub:
                sub.Text = Tr(sub.Text) ?? "";
                foreach (var item in sub.Items) Walk(item, depth + 1);
                return;
            case MenuFlyoutItem mi:
                mi.Text = Tr(mi.Text) ?? "";
                return;
            case Flyout flyout:
                Walk(flyout.Content, depth + 1);
                return;
            case UserControl uc:
                Walk(uc.Content, depth + 1);
                return;
            case Panel panel:
                foreach (var child in panel.Children) Walk(child, depth + 1);
                return;
            case Border border:
                Walk(border.Child, depth + 1);
                return;
            case Viewbox vb:
                Walk(vb.Child, depth + 1);
                return;
            case ItemsControl items:
                foreach (var item in items.Items) Walk(item, depth + 1);
                return;
        }
    }
}
