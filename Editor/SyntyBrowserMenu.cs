namespace Editor.Tools.SyntyBrowser;

internal static class SyntyBrowserMenu
{
	[Menu( "Editor", "Tools/Synty Browser", "view_in_ar" )]
	public static void Open()
	{
		SyntyBrowserWindow.OpenDock();
	}
}
