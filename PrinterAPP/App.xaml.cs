namespace PrinterAPP
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // Follow the system light/dark theme. Page/card backgrounds and text now use the
            // craft AppThemeBinding tokens (Colors.xaml + Styles.xaml), so dark mode renders
            // correctly instead of the old white-text-on-white-card problem that forced Light.
            UserAppTheme = AppTheme.Unspecified;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}
