using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NovaVpn;

public static class Program
{
	private static WindowsUserInstance instance;

	[STAThread]
	public static void Main(string[] args)
	{
		string text = null;
		bool windowsStartup = args.Contains("--windows-startup");
		bool backgroundConnect = windowsStartup && args.Contains("--background-connect");
		string text2 = "home";
		string snapshotTheme = null;
		string snapshotBackground = null;
		string snapshotDensity = null;
		string snapshotServerView = null;
		double snapshotScroll = 0;
		double snapshotTextScale = 0;
        double snapshotWidth = 0, snapshotHeight = 0;
		bool snapshotHighContrast = false;
		for (int i = 0; i + 1 < args.Length; i++)
		{
			if (string.Equals(args[i], "--snapshot", StringComparison.OrdinalIgnoreCase))
			{
				text = args[i + 1];
			}
			if (string.Equals(args[i], "--page", StringComparison.OrdinalIgnoreCase))
			{
				text2 = args[i + 1];
			}
			if (string.Equals(args[i], "--width", StringComparison.OrdinalIgnoreCase)) double.TryParse(args[i + 1], out snapshotWidth);
            if (string.Equals(args[i], "--height", StringComparison.OrdinalIgnoreCase)) double.TryParse(args[i + 1], out snapshotHeight);
            if (string.Equals(args[i], "--theme", StringComparison.OrdinalIgnoreCase)) snapshotTheme = args[i + 1];
			if (string.Equals(args[i], "--background", StringComparison.OrdinalIgnoreCase)) snapshotBackground = args[i + 1];
			if (string.Equals(args[i], "--density", StringComparison.OrdinalIgnoreCase)) snapshotDensity = args[i + 1];
			if (string.Equals(args[i], "--server-view", StringComparison.OrdinalIgnoreCase)) snapshotServerView = args[i + 1];
			if (string.Equals(args[i], "--scroll", StringComparison.OrdinalIgnoreCase)) double.TryParse(args[i + 1], out snapshotScroll);
			if (string.Equals(args[i], "--text-scale", StringComparison.OrdinalIgnoreCase)) double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out snapshotTextScale);
			if (string.Equals(args[i], "--high-contrast", StringComparison.OrdinalIgnoreCase)) bool.TryParse(args[i + 1], out snapshotHighContrast);
		}
		try { instance = WindowsUserInstance.TryAcquire(text == null ? "Desktop" : "Snapshot"); }
        catch (Exception error) when (error is UnauthorizedAccessException || error is System.Security.SecurityException || error is WaitHandleCannotBeOpenedException || error is LegacyInstanceDetectedException)
        {
            if (!windowsStartup) MessageBox.Show("Не удалось проверить экземпляр NOVA для вашей учётной записи Windows. " + error.Message, "NOVA VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
		if (instance == null)
		{
			if (windowsStartup) return;
			MessageBox.Show("NOVA уже запущена для вашей учётной записи Windows, возможно в другом сеансе.", "NOVA VPN", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
        FileStream installation = null;
		try {
        try { if (text == null) installation = WindowsInstallationLease.AcquireClient(Path.GetDirectoryName(typeof(Program).Assembly.Location)); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException || error is ArgumentException)
        {
            if (!windowsStartup) MessageBox.Show("Не удалось открыть установку NOVA. Завершите обновление или повторите установку. " + error.Message, "NOVA VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
		ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
		Application application = new Application();
		application.ShutdownMode = ShutdownMode.OnMainWindowClose;
		application.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
		{
			if (!backgroundConnect) MessageBox.Show("Произошла ошибка:\n" + e.Exception.Message, "NOVA VPN", MessageBoxButton.OK, MessageBoxImage.Hand);
			e.Handled = true;
		};
		MainWindow window = new MainWindow(text != null);
		window.WindowsStartupLaunch = windowsStartup;
		application.MainWindow = window;
		if (!string.IsNullOrWhiteSpace(text))
		{
			string target = text;
            if (snapshotWidth >= window.MinWidth && snapshotWidth <= 3840) window.Width = snapshotWidth;
            if (snapshotHeight >= window.MinHeight && snapshotHeight <= 2160) window.Height = snapshotHeight;
			string page = text2;
			string theme = snapshotTheme;
			string background = snapshotBackground;
			string density = snapshotDensity;
			string serverView = snapshotServerView;
			double scrollOffset = snapshotScroll;
			double textScale = snapshotTextScale;
			bool highContrast = snapshotHighContrast;
			window.Loaded += delegate
			{
				window.Dispatcher.BeginInvoke((Action)delegate
				{
					try
					{
						window.ConfigureSnapshot(theme, background, density, serverView, textScale, highContrast);
						Window captureWindow = window;
						if (string.Equals(page, "appearance", StringComparison.OrdinalIgnoreCase)) { captureWindow = window.CreateAppearanceSnapshotWindow(); captureWindow.Show(); }
						else window.ShowPageForSnapshot(page);
						captureWindow.UpdateLayout();
						if (captureWindow is AppearanceWindow appearance) appearance.ScrollForSnapshot(scrollOffset);
						int pixelWidth = Math.Max(1, (int)Math.Round(captureWindow.ActualWidth));
						int pixelHeight = Math.Max(1, (int)Math.Round(captureWindow.ActualHeight));
						RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96.0, 96.0, PixelFormats.Pbgra32);
						renderTargetBitmap.Render(captureWindow);
						PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder
						{
							Frames = { BitmapFrame.Create(renderTargetBitmap) }
						};
						string directoryName = Path.GetDirectoryName(Path.GetFullPath(target));
						if (!string.IsNullOrWhiteSpace(directoryName))
						{
							Directory.CreateDirectory(directoryName);
						}
						using FileStream stream = File.Create(target);
						pngBitmapEncoder.Save(stream);
					}
					finally
					{
						Application.Current.Shutdown();
					}
				}, DispatcherPriority.ApplicationIdle);
			};
		}
		if (backgroundConnect && text == null) {
			window.ShowInTaskbar = false;
			application.Dispatcher.BeginInvoke((Action)(async () => await window.RunWindowsBackgroundStartupAsync()));
			application.Run();
		} else application.Run(window);
		GC.KeepAlive(instance);
        } finally { try { installation?.Dispose(); } finally { instance.Dispose(); instance = null; } }
	}
}
