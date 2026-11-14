using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;
using ImmichSplash.App.ViewModels;

namespace ImmichSplash.App.Controls;

/// <summary>
/// The six-box PIN field. <see cref="Pin"/> is two-way; the view model reacts when it reaches six digits.
/// It takes the focus whenever it's on screen and <see cref="IsEntryEnabled"/>, so you can just type.
/// </summary>
public partial class PinEntry : UserControl
{
    public const int Length = 6;

    public static readonly StyledProperty<string> PinProperty =
        AvaloniaProperty.Register<PinEntry, string>(nameof(Pin), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsEntryEnabledProperty =
        AvaloniaProperty.Register<PinEntry, bool>(nameof(IsEntryEnabled), true);

    public string Pin
    {
        get => GetValue(PinProperty);
        set => SetValue(PinProperty, value);
    }

    /// <summary>False while checking the PIN: typing pauses and no box is outlined.</summary>
    public bool IsEntryEnabled
    {
        get => GetValue(IsEntryEnabledProperty);
        set => SetValue(IsEntryEnabledProperty, value);
    }

    /// <summary>Enter was pressed.</summary>
    public event EventHandler? Submitted;

    readonly TextBox _input;
    readonly ItemsControl _boxes;
    bool _syncing;

    public PinEntry()
    {
        InitializeComponent();
        _input = this.FindControl<TextBox>("Input")!;
        _boxes = this.FindControl<ItemsControl>("Boxes")!;
        _input.GotFocus += (_, _) => _boxes.Classes.Set("typing", true);
        _input.LostFocus += (_, _) => _boxes.Classes.Set("typing", false);
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Submitted?.Invoke(this, EventArgs.Empty);
            }
        };
        _input.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_syncing)
                Pin = new string((_input.Text ?? "").Where(char.IsAsciiDigit).Take(Length).ToArray());
        };
        // Coming back to a page (pages are kept while you visit others): the field gets a place on screen again.
        var onScreen = false;
        _input.EffectiveViewportChanged += (_, e) =>
        {
            var now = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
            if (now && !onScreen)
                FocusSoon();
            onScreen = now;
        };
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PinProperty || change.Property == IsEntryEnabledProperty || change.Property == IsVisibleProperty)
        {
            Refresh();
            FocusSoon();
        }
    }

    void Refresh()
    {
        var pin = Pin ?? "";
        if (_input.Text != pin)
        {
            _syncing = true;
            _input.Text = pin;
            _input.CaretIndex = pin.Length;
            _syncing = false;
        }
        _input.IsReadOnly = !IsEntryEnabled;
        _boxes.ItemsSource = Enumerable.Range(0, Length)
            .Select(i => new PinBox(i < pin.Length, IsEntryEnabled && i == Math.Min(pin.Length, Length - 1))).ToList();
    }

    /// <summary>
    /// Focuses the field once layout has settled. Clicking "Locked folder" in the sidebar leaves the focus on the
    /// sidebar, which happens after the page appears, so it's tried once more a moment later.
    /// </summary>
    public void FocusSoon()
    {
        void Try()
        {
            if (IsEntryEnabled && _input.IsEffectivelyVisible && !_input.IsFocused)
                _input.Focus();
        }
        Dispatcher.UIThread.Post(Try, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(Try, TimeSpan.FromMilliseconds(200));
    }
}
