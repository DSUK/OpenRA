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
using System.Runtime.InteropServices;
using System.Threading;
using OpenRA.Widgets;
using CString=System.IntPtr;
using Unshield=System.IntPtr;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	public class InstallFromTTFDLogic
	{
		[DllImport("unshield.dll", EntryPoint="unshield_open_force_version")]
		public extern static Unshield UnshieldOpenForceVersion(string filename, int version);

		[DllImport("unshield.dll", EntryPoint="unshield_set_log_level")]
		public extern static void UnshieldSetLogLevel(int log_level);

		[DllImport("unshield.dll", EntryPoint="unshield_file_name")]
		public extern static CString UnshieldFileName(Unshield cab_file,int index);

		[DllImport("unshield.dll", EntryPoint="unshield_file_save")]
		public extern static int UnshieldFileExtract(Unshield cab_file,int index,string filename);

		[DllImport("unshield.dll", EntryPoint="unshield_close")]
		public extern static void UnshieldClose(Unshield cab_file);

		readonly Widget panel;
		readonly ProgressBarWidget progressBar;
		readonly LabelWidget statusLabel;
		readonly Action continueLoading;
		readonly ButtonWidget retryButton, backButton;
		readonly Widget installingContainer, insertDiskContainer;
		readonly ContentInstaller installData;

		[ObjectCreator.UseCtor]
		public InstallFromTTFDLogic(Widget widget, Action continueLoading)
		{
			installData = Game.ModData.Manifest.Get<ContentInstaller>();
			this.continueLoading = continueLoading;
			panel = widget.Get("INSTALL_FROMCD_PANEL");
			progressBar = panel.Get<ProgressBarWidget>("PROGRESS_BAR");
			statusLabel = panel.Get<LabelWidget>("STATUS_LABEL");

			backButton = panel.Get<ButtonWidget>("BACK_BUTTON");
			backButton.OnClick = Ui.CloseWindow;

			retryButton = panel.Get<ButtonWidget>("RETRY_BUTTON");
			retryButton.OnClick = CheckForDisk;

			installingContainer = panel.Get("INSTALLING");
			insertDiskContainer = panel.Get("INSERT_DISK");
			CheckForDisk();
		}

		bool IsValidDisk(string diskRoot)
		{
			return installData.DiskTestFiles.All(f => File.Exists(Path.Combine(diskRoot, f)));
		}
		bool IsTTFD(string diskpath) {
			bool test = File.Exists(Path.Combine(diskpath,"data1.hdr"));
			int i = 0;
			while(test && i < 14)
			{
				test &= File.Exists(Path.Combine(diskpath,String.Format("data{0}.cab",++i)));
			}
			return test;	
		}

		void CheckForDisk()
		{
			var path = InstallUtils.GetMountedDisk(IsValidDisk);

			if (path != null) {
				Install(path);
			} else if((path = InstallUtils.GetMountedDisk(IsTTFD)) !=null) {
				InstallTTFD(Platform.ResolvePath(path,"data1.cab"));
			} else {
				insertDiskContainer.IsVisible = () => true;
				installingContainer.IsVisible = () => false;
			}
		}
		void InstallTTFD(string source) {
			//backButton.IsDisabled = () => true;
			retryButton.IsDisabled = () => true;
			insertDiskContainer.IsVisible = () => false;
			installingContainer.IsVisible = () => true;

			UnshieldSetLogLevel(0);
			Unshield cab_file  = UnshieldOpenForceVersion(source + '\0', -1);
			foreach(int index in installData.TTFDIndexes){
				string filename = Marshal.PtrToStringAnsi(UnshieldFileName(cab_file,index));
				string dest = Platform.ResolvePath("^", "Content", Game.ModData.Manifest.Mod.Id, filename.ToLower());
				UnshieldFileExtract(cab_file,index,dest + '\0');
			}	
			UnshieldClose(cab_file);
			continueLoading();
		}

		void Install(string source)
		{
			backButton.IsDisabled = () => true;
			retryButton.IsDisabled = () => true;
			insertDiskContainer.IsVisible = () => false;
			installingContainer.IsVisible = () => true;

			var dest = Platform.ResolvePath("^", "Content", Game.ModData.Manifest.Mod.Id);
			var copyFiles = installData.CopyFilesFromCD;

			var packageToExtract = installData.PackageToExtractFromCD.Split(':');
			var extractPackage = packageToExtract.First();
			var annotation = packageToExtract.Length > 1 ? packageToExtract.Last() : null;

			var extractFiles = installData.ExtractFilesFromCD;

			var installCounter = 0;
			var installTotal = copyFiles.Length + extractFiles.Length;
			var onProgress = (Action<string>)(s => Game.RunAfterTick(() =>
			{
				progressBar.Percentage = installCounter * 100 / installTotal;
				installCounter++;

				statusLabel.GetText = () => s;
			}));

			var onError = (Action<string>)(s => Game.RunAfterTick(() =>
			{
				statusLabel.GetText = () => "Error: " + s;
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
						statusLabel.GetText = () => "Game assets have been extracted.";
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
