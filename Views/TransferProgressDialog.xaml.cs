using System.ComponentModel;
using System.Windows;
using BeetsBackup.ViewModels;

namespace BeetsBackup.Views;

/// <summary>
/// Non-modal floating window that shows a large circular progress ring during an active transfer.
/// Docks itself to the right edge of its owner window and tracks owner position/size changes.
/// Closing the window hides it but does NOT cancel the transfer — cancellation is only via the Stop button.
/// </summary>
public partial class TransferProgressDialog : Window
{
    private readonly MainViewModel _vm;
    private readonly Window _owner;
    private bool _trulyClose;

    /// <summary>
    /// Becomes <c>true</c> the instant a real close is in progress. Owners should treat an instance
    /// with this flag set as disposed and build a fresh dialog rather than calling <see cref="Window.Show"/>.
    /// WPF forbids Show() on a window whose Close() has started, and <see cref="Window.IsLoaded"/> /
    /// <see cref="Window.IsVisible"/> don't flip synchronously when Close() is called from a property-change
    /// handler, so we surface this explicit flag to close the race.
    /// </summary>
    public bool IsShuttingDown { get; private set; }

    public TransferProgressDialog(MainViewModel viewModel, Window owner)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;
        _owner = owner;
        Owner = owner;

        // Re-dock whenever the owner moves or resizes
        owner.LocationChanged += OwnerOnBoundsChanged;
        owner.SizeChanged += OwnerOnBoundsChanged;

        // Hide the dialog automatically when the transfer ends
        _vm.PropertyChanged += OnVmPropertyChanged;

        Loaded += (_, _) => CenterOnOwner();
    }

    /// <summary>
    /// Centers the dialog over the owner window. Tracks owner moves so the dialog
    /// stays centered if the user drags the main window during a transfer.
    /// </summary>
    private void CenterOnOwner()
    {
        if (_owner.WindowState == WindowState.Minimized) return;

        Left = _owner.Left + (_owner.ActualWidth  - ActualWidth)  / 2;
        Top  = _owner.Top  + (_owner.ActualHeight - ActualHeight) / 2;
    }

    private void OwnerOnBoundsChanged(object? sender, EventArgs e) => CenterOnOwner();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsTransferring)) return;
        if (!_vm.IsTransferring)
        {
            // Auto-close when transfer ends so next transfer gets a fresh dialog
            _trulyClose = true;
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Pressing the window X should hide, not cancel the transfer.
        // Only close for real when the transfer has ended or we're shutting down.
        if (!_trulyClose && _vm.IsTransferring)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Mark SYNCHRONOUSLY so any re-entrant IsTransferring=true in the owner can tell
        // "this instance is going away — build a new one" without waiting for the async Closed event.
        IsShuttingDown = true;

        // Unhook unconditionally so handlers don't leak if base.OnClosing (or a subclass override)
        // throws — e.g. during application shutdown racing with a transfer.
        try
        {
            _owner.LocationChanged -= OwnerOnBoundsChanged;
            _owner.SizeChanged -= OwnerOnBoundsChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        finally
        {
            base.OnClosing(e);
        }
    }
}
