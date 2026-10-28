using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ProJict.App.Controls;

/// <summary>
/// ItemsControl whose templated content (row text boxes, buttons) is visible to UI Automation and screen readers;
/// the stock peer hides children of non-selector item containers.
/// </summary>
public sealed class OutlineItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
