namespace PrinterAPP
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // Pin Light theme. The UI is light-only by design — page/card backgrounds are
            // hardcoded White/#F5F5F5, while text uses AppThemeBinding (Light=dark text,
            // Dark=white text). On a device set to dark mode this produced white text on
            // white cards (invisible inputs/labels). Forcing Light keeps text/background
            // contrast correct on every Android/Windows device regardless of system theme.
            // (Proper dark-mode support would require theming all backgrounds — deferred.)
            UserAppTheme = AppTheme.Light;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}
