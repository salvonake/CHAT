using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Poseidon.Desktop.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace Poseidon.Desktop.Views
{
    public partial class ChatView : UserControl
    {
        public ChatView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Auto-scroll to bottom when new messages arrive
            if (DataContext is ChatViewModel vm)
            {
                vm.Messages.CollectionChanged += OnMessagesChanged;
            }

            // Focus the input box
            InputBox?.Focus();
        }

        private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Scroll to bottom
            Dispatcher.InvokeAsync(() =>
            {
                MessagesScroll?.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}

// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
//  Value Converters for chat bubbles
// â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

namespace Poseidon.Desktop.ViewModels
{
    /// <summary>Inverts a boolean value.</summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is bool b ? !b : value;
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => value is bool b ? !b : value;
    }

    /// <summary>Userâ†’Right, Assistant/Systemâ†’Left (in RTL, so visually swapped).</summary>
    public class ChatRoleToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is ChatRole role && role == ChatRole.User
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotSupportedException();
    }

    /// <summary>Margin: User bubbles get left-margin, others get right-margin (RTL-aware).</summary>
    public class ChatRoleToMarginConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is ChatRole role && role == ChatRole.User
                ? new Thickness(120, 0, 0, 0)
                : new Thickness(0, 0, 120, 0);
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotSupportedException();
    }

    /// <summary>Background brush per role.</summary>
    public class ChatRoleToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush UserBrush = new(Color.FromRgb(0x0D, 0x47, 0xA1)); // Deep blue
        private static readonly SolidColorBrush AssistantBrush = new(Color.FromRgb(0x2D, 0x2D, 0x30)); // Dark surface
        private static readonly SolidColorBrush SystemBrush = new(Color.FromRgb(0x1B, 0x5E, 0x20)); // Dark green

        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is ChatRole role
                ? role switch
                {
                    ChatRole.User => UserBrush,
                    ChatRole.System => SystemBrush,
                    _ => AssistantBrush
                }
                : AssistantBrush;
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotSupportedException();
    }

    /// <summary>Foreground text color per role.</summary>
    public class ChatRoleToFgConverter : IValueConverter
    {
        private static readonly SolidColorBrush Light = new(Color.FromRgb(0xE0, 0xE0, 0xE0));

        public object Convert(object value, Type t, object p, CultureInfo c)
            => Light;
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotSupportedException();
    }

    /// <summary>Corner radius â€” rounds opposite corners for chat-bubble look.</summary>
    public class ChatRoleToCornerConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c)
            => value is ChatRole role && role == ChatRole.User
                ? new CornerRadius(12, 2, 12, 12)   // User: sharp top-left (RTL: top-right visually)
                : new CornerRadius(2, 12, 12, 12);   // Assistant: sharp top-right
        public object ConvertBack(object value, Type t, object p, CultureInfo c)
            => throw new NotSupportedException();
    }
}

