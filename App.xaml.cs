using System.Configuration;
using System.Data;
using System.Windows;
using FellowOakDicom;
using FellowOakDicom.Imaging;

namespace DicomStView
{

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		new DicomSetupBuilder()
			.RegisterServices(services => services.AddFellowOakDicom().AddImageManager<WinFormsImageManager>())
			.Build();

		base.OnStartup(e);
	}
}
}

