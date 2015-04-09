#region Copyright & License Information
/*
 * Copyright 2007-2015 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.IO;
using OpenRA.FileSystem;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class InstallFromTFDLogic
	{
		readonly Widget panel;
		readonly Action continueLoading;
		readonly ButtonWidget retryButton, backButton;
		readonly ContentInstaller installData;

		[ObjectCreator.UseCtor]
		public InstallFromTFDLogic(Widget widget, Action continueLoading)
		{
			installData = Game.ModData.Manifest.Get<ContentInstaller>();
			this.continueLoading = continueLoading;
			panel = widget.Get("INSTALL_FROM_TFD_PANEL");

			backButton = panel.Get<ButtonWidget>("BACK_BUTTON");
			backButton.OnClick = Ui.CloseWindow;

			retryButton = panel.Get<ButtonWidget>("RETRY_BUTTON");
			retryButton.OnClick = CheckForDisk;

			CheckForDisk();
		}

		bool IsTFD(string diskpath)
		{
			bool test = File.Exists(Path.Combine(diskpath, "data1.hdr"));
			int i = 0;
			while (test && i < 14)
			{
				test &= File.Exists(Path.Combine(diskpath, "data{0}.cab".F(++i)));
			}

			return test;
		}

		void CheckForDisk()
		{
			var path = InstallUtils.GetMountedDisk(IsTFD);
			if (path != null)
			{
				InstallTFD(Platform.ResolvePath(path, "data1.hdr"));
			}
		}

		void InstallTFD(string source)
		{
			retryButton.IsDisabled = () => true;
			using (var cab_ex = new InstallShieldCABExtractor(source))
			{
				foreach (uint index in installData.TFDIndexes)
				{
					string filename = cab_ex.FileName(index);
					string dest = Platform.ResolvePath("^", "Content", Game.ModData.Manifest.Mod.Id, filename.ToLower());
					cab_ex.ExtractFile(index, dest);
				}
			}

			continueLoading();
		}
	}
}
