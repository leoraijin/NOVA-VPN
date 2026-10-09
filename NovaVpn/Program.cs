using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NovaVpn;

public static class Program
{
	private static Mutex mutex;

	[STAThread]
	public static void Main(string[] args)
	{
		string text = null;
		string text2 = "home";
		string snapshotTheme = null;
		string snapshotBackground = null;
		string snapshotDensity = null;
		string snapshotServerView = null;
		double snapshotScroll = 0;
		double snapshotTextScale = 0;
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
			if (string.Equals(args[i], "--theme", StringComparison.OrdinalIgnoreCase)) snapshotTheme = args[i + 1];
			if (string.Equals(args[i], "--background", StringComparison.OrdinalIgnoreCase)) snapshotBackground = args[i + 1];
			if (string.Equals(args[i], "--density", StringComparison.OrdinalIgnoreCase)) snapshotDensity = args[i + 1];
			if (string.Equals(args[i], "--server-view", StringComparison.OrdinalIgnoreCase)) snapshotServerView = args[i + 1];
			if (string.Equals(args[i], "--scroll", StringComparison.OrdinalIgnoreCase)) double.TryParse(args[i + 1], out snapshotScroll);
			if (string.Equals(args[i], "--text-scale", StringComparison.OrdinalIgnoreCase)) double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out snapshotTextScale);
			if (string.Equals(args[i], "--high-contrast", StringComparison.OrdinalIgnoreCase)) bool.TryParse(args[i + 1], out snapshotHighContrast);
		}
		mutex = new Mutex(initiallyOwned: true, (text == null) ? "NOVA-VPN-Desktop-Instance" : "NOVA-VPN-Snapshot-Instance", out var createdNew);
		if (!createdNew)
		{
			MessageBox.Show("NOVA уже запущена.", "NOVA VPN", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
		Application application = new Application();
		application.ShutdownMode = ShutdownMode.OnMainWindowClose;
		application.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
		{
			MessageBox.Show("Произошла ошибка:\n" + e.Exception.Message, "NOVA VPN", MessageBoxButton.OK, MessageBoxImage.Hand);
			e.Handled = true;
		};
		MainWindow window = new MainWindow(text != null);
		application.MainWindow = window;
		if (!string.IsNullOrWhiteSpace(text))
		{
			string target = text;
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
		application.Run(window);
		GC.KeepAlive(mutex);
	}
}
