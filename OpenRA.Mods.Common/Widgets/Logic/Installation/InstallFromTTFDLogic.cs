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
using System.Linq;
using System.Threading;
using OpenRA.FileSystem;
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class InstallFromTTFDLogic
	{
		readonly Widget panel;
		readonly Action continueLoading;
		readonly ButtonWidget retryButton, backButton;
		readonly ContentInstaller installData;

		[ObjectCreator.UseCtor]
		public InstallFromTTFDLogic(Widget widget, Action continueLoading)
		{
			installData = Game.ModData.Manifest.Get<ContentInstaller>();
			this.continueLoading = continueLoading;
			panel = widget.Get("INSTALL_FROM_TTFD_PANEL");

			backButton = panel.Get<ButtonWidget>("BACK_BUTTON");
			backButton.OnClick = Ui.CloseWindow;

			retryButton = panel.Get<ButtonWidget>("RETRY_BUTTON");
			retryButton.OnClick = CheckForDisk;

			CheckForDisk();
		}

		bool IsValidDisk(string diskRoot)
		{
			return installData.DiskTestFiles.All(f => File.Exists(Path.Combine(diskRoot, f)));
		}

		bool IsTTFD(string diskpath) {
			bool test = File.Exists(Path.Combine(diskpath, "data1.hdr"));
			int i = 0;
			while (test && i < 14)
			{
				test &= File.Exists(Path.Combine(diskpath, string.Format("data{0}.cab", ++i)));
			}

			return test;
		}

		void CheckForDisk()
		{
			var path = InstallUtils.GetMountedDisk(IsValidDisk);

			if (path != null) {
				Install(path);
			} else if ((path = InstallUtils.GetMountedDisk(IsTTFD)) != null) {
				InstallTTFD(Platform.ResolvePath(path, "data1.hdr"));
			} else {
			}
		}

		void InstallTTFD(string source) {
			////backButton.IsDisabled = () => true;
			retryButton.IsDisabled = () => true;
			using (var cab_ex = new InstallShieldCABExtractor(source)) {
				foreach (uint index in installData.TTFDIndexes) {
					string filename = cab_ex.FileName(index);
					string dest = Platform.ResolvePath("^", "Content",
						Game.ModData.Manifest.Mod.Id, filename.ToLower());
					cab_ex.ExtractFile(index, dest);
				}
			}

			continueLoading();
		}

		void Install(string source)
		{
			backButton.IsDisabled = () => true;
			retryButton.IsDisabled = () => true;

			var dest = Platform.ResolvePath("^", "Content", Game.ModData.Manifest.Mod.Id);
			var copyFiles = installData.CopyFilesFromCD;

			var packageToExtract = installData.PackageToExtractFromCD.Split(':');
			var extractPackage = packageToExtract.First();
			var annotation = packageToExtract.Length > 1 ? packageToExtract.Last() : null;

			var extractFiles = installData.ExtractFilesFromCD;

			var installCounter = 0;
			var onProgress = (Action<string>)(s => Game.RunAfterTick(() =>
			{
				installCounter++;
			}));

			var onError = (Action<string>)(s => Game.RunAfterTick(() =>
			{
				backButton.IsDisabled = () => false;
				retryButton.IsDisabled = () => false;
			}));

			new Thread(() =>
			{
				try
				{
					if (!InstallUtils.CopyFiles(source, copyFiles, dest, onProgress, onError))
					{
						onError("Copying files from CD failed.");
						return;
					}

					if (!string.IsNullOrEmpty(extractPackage))
					{
						if (!InstallUtils.ExtractFromPackage(source, extractPackage, annotation, extractFiles, dest, onProgress, onError))
						{
							onError("Extracting files from CD failed.");
							return;
						}
					}

					Game.RunAfterTick(() =>
					{
						Ui.CloseWindow();
						continueLoading();
					});
				}
				catch (Exception e)
				{
					onError("Installation failed.\n{0}".F(e.Message));
					Log.Write("debug", e.ToString());
					return;
				}
			}) { IsBackground = true }.Start();
		}
	}
}
