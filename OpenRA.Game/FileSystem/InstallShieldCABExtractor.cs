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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
namespace OpenRA.FileSystem
{
	public class InstallShieldCABExtractor : IDisposable, IFolder
	{
		const uint FileSplit         = 0x1;
		const uint FileObfuscated    = 0x2;
		const uint FileCompressed    = 0x4;
		const uint FileInvalid       = 0x8;

		const uint LinkPrev          = 0x1;
		const uint LinkNext          = 0x2;
		const uint MaxFileGroupCount = 71;
#region Nested Structs
		struct FileGroup
		{
			public readonly string Name;
			public readonly uint FirstFile;
			public readonly uint LastFile;
			public FileGroup(BinaryReader reader, long offset)
			{
				var nameOffset = reader.ReadUInt32();
				/*   unknown  */ reader.ReadBytes(0x12);
				FirstFile      = reader.ReadUInt32();
				LastFile       = reader.ReadUInt32();

				reader.BaseStream.Seek(offset + (long)nameOffset, SeekOrigin.Begin);
				Name = reader.BaseStream.ReadASCIIZ();
			}
		}

		struct VolumeHeader
		{
			public uint DataOffset;
			public uint DataOffsetHigh;
			public uint FirstFileIndex;
			public uint LastFileIndex;

			public uint FirstFileOffset;
			public uint FirstFileOffsetHigh;
			public uint FirstFileSizeExpanded;
			public uint FirstFileSizeExpandedHigh;

			public uint FirstFileSizeCompressed;
			public uint FirstFileSizeCompressedHigh;
			public uint LastFileOffset;
			public uint LastFileOffsetHigh;

			public uint LastFileSizeExpanded;
			public uint LastFileSizeExpandedHigh;
			public uint LastFileSizeCompressed;
			public uint LastFileSizeCompressedHigh;

			public VolumeHeader(BinaryReader reader)
			{
				DataOffset      = reader.ReadUInt32();
				DataOffsetHigh  = reader.ReadUInt32();

				FirstFileIndex      = reader.ReadUInt32();
				LastFileIndex       = reader.ReadUInt32();
				FirstFileOffset     = reader.ReadUInt32();
				FirstFileOffsetHigh = reader.ReadUInt32();

				FirstFileSizeExpanded       = reader.ReadUInt32();
				FirstFileSizeExpandedHigh   = reader.ReadUInt32();
				FirstFileSizeCompressed     = reader.ReadUInt32();
				FirstFileSizeCompressedHigh = reader.ReadUInt32();

				LastFileOffset           = reader.ReadUInt32();
				LastFileOffsetHigh       = reader.ReadUInt32();
				LastFileSizeExpanded     = reader.ReadUInt32();
				LastFileSizeExpandedHigh = reader.ReadUInt32();

				LastFileSizeCompressed     = reader.ReadUInt32();
				LastFileSizeCompressedHigh = reader.ReadUInt32();
			}
		}

		struct CommonHeader
		{
			public readonly uint Version;
			public readonly uint VolumeInfo;
			public readonly long CabDescriptorOffset;
			public readonly uint CabDescriptorSize;

			public CommonHeader(BinaryReader reader)
			{
				Version             = reader.ReadUInt32();
				VolumeInfo          = reader.ReadUInt32();
				CabDescriptorOffset = (long)reader.ReadUInt32();
				CabDescriptorSize   = reader.ReadUInt32();
			}
		}

		struct CabDescriptor
		{
			public readonly long FileTableOffset;
			public readonly uint FileTableSize;
			public readonly uint FileTableSize2;
			public readonly uint DirectoryCount;

			public readonly uint FileCount;
			public readonly long FileTableOffset2;

			public CabDescriptor(BinaryReader reader, CommonHeader commonHeader)
			{
				reader.BaseStream.Seek(commonHeader.CabDescriptorOffset + 0xC, SeekOrigin.Begin);
				FileTableOffset = (long)reader.ReadUInt32();
				/*    unknown  */ reader.ReadUInt32();
				FileTableSize   = reader.ReadUInt32();

				FileTableSize2 = reader.ReadUInt32();
				DirectoryCount = reader.ReadUInt32();
				/*   unknown  */ reader.ReadUInt64();
				FileCount      = reader.ReadUInt32();

				FileTableOffset2 = (long)reader.ReadUInt32();
			}
		}

		struct FileDescriptor
		{
			public readonly ushort	Flags;
			public readonly uint	ExpandedSize;
			public readonly uint	CompressedSize;
			public readonly uint	DataOffset;

			public readonly byte[]	MD5;
			public readonly uint	NameOffset;
			public readonly ushort	DirectoryIndex;
			public readonly uint	LinkPrevious;

			public readonly uint	LinkNext;
			public readonly byte	LinkFlags;
			public readonly ushort	Volume;
			public readonly string	Filename;

			public FileDescriptor(BinaryReader reader, long tableOffset)
			{
				Flags            = reader.ReadUInt16();
				ExpandedSize     = reader.ReadUInt32();
				/*    unknown   */ reader.ReadUInt32();
				CompressedSize   = reader.ReadUInt32();

				/*  unknown */ reader.ReadUInt32();
				DataOffset   = reader.ReadUInt32();
				/*  unknown */ reader.ReadUInt32();
				MD5          = reader.ReadBytes(0x10);

				/*   unknown  */ reader.ReadBytes(0x10);
				NameOffset     = reader.ReadUInt32();
				DirectoryIndex = reader.ReadUInt16();
				/*   unknown  */ reader.ReadBytes(0xc);
				LinkPrevious   = reader.ReadUInt32();
				LinkNext       = reader.ReadUInt32();

				LinkFlags   = reader.ReadByte();
				Volume      = reader.ReadUInt16();
				var posSave = reader.BaseStream.Position;

				reader.BaseStream.Seek(tableOffset + NameOffset, SeekOrigin.Begin);
				Filename = reader.BaseStream.ReadASCIIZ();
				reader.BaseStream.Seek(posSave, SeekOrigin.Begin);
			}
		}
#endregion
		readonly Stream hdrFile;
		CommonHeader commonHeader;
		CabDescriptor cabDescriptor;
		List<uint> directoryTable;
		Dictionary<uint, string> directoryNames;
		Dictionary<uint, FileDescriptor> fileDescriptors;
		Dictionary<string, FileDescriptor> fileDict;
		List<uint> fileGroupOffsets;
		List<FileGroup> fileGroups;
		int priority;
		string commonName;
		public int Priority { get { return priority; } }

		public string Name { get { return commonName; } }

		public InstallShieldCABExtractor(string hdrFilename, int priority = -1)
		{
			this.priority = priority;
			hdrFile       = GlobalFileSystem.Open(hdrFilename);
			var buff      = new List<char>(hdrFilename.Substring(0, hdrFilename.LastIndexOf('.')).ToCharArray());

			for (int i = buff.Count - 1; char.IsNumber(buff[i]); --i)
				buff.RemoveAt(i);

			commonName    = new string(buff.ToArray());
			var reader    = new BinaryReader(hdrFile);
			var signature = reader.ReadUInt32();

			if (signature != 0x28635349)
				throw new InvalidDataException("Not an Installshield CAB package");

			commonHeader     = new CommonHeader(reader);
			cabDescriptor    = new CabDescriptor(reader, commonHeader);
			/*    unknown   */ reader.ReadBytes(0xe);
			fileGroupOffsets = new List<uint>();

			for (uint i = MaxFileGroupCount; i > 0; --i)
				fileGroupOffsets.Add(reader.ReadUInt32());

			reader.BaseStream.Seek(commonHeader.CabDescriptorOffset + cabDescriptor.FileTableOffset, SeekOrigin.Begin);
			directoryTable = new List<uint>();

			for (uint i = cabDescriptor.DirectoryCount; i > 0; --i)
				directoryTable.Add(reader.ReadUInt32());

			fileGroups = new List<FileGroup>();
			foreach (uint offset in fileGroupOffsets)
			{
				var nextOffset = offset;
				while (nextOffset != 0)
				{
					reader.BaseStream.Seek((long)nextOffset + 4 + commonHeader.CabDescriptorOffset, SeekOrigin.Begin);
					var descriptorOffset = reader.ReadUInt32();
					nextOffset           = reader.ReadUInt32();
					reader.BaseStream.Seek((long)descriptorOffset + commonHeader.CabDescriptorOffset, SeekOrigin.Begin);

					fileGroups.Add(new FileGroup(reader, commonHeader.CabDescriptorOffset));
				}
			}

			directoryNames  = new Dictionary<uint, string>();
			fileDescriptors = new Dictionary<uint, FileDescriptor>();
			reader.BaseStream.Seek(commonHeader.CabDescriptorOffset + cabDescriptor.FileTableOffset + cabDescriptor.FileTableOffset2, SeekOrigin.Begin);
			fileDict = new Dictionary<string, FileDescriptor>();
			foreach (var fileGroup in fileGroups)
			{
				for (uint index = fileGroup.FirstFile; index <= fileGroup.LastFile; ++index)
				{
					AddFileDescriptorToList(index);
					var fileDescriptor = fileDescriptors[index];
					var fullFilePath   = "{0}\\{1}\\{2}".F(fileGroup.Name, DirectoryName((uint)fileDescriptor.DirectoryIndex), fileDescriptor.Filename);
					fileDict.Add(fullFilePath, fileDescriptor);
				}
			}
		}

		public string DirectoryName(uint index)
		{
			if (directoryNames.ContainsKey(index))
				return directoryNames[index];
			var reader = new BinaryReader(hdrFile);
			reader.BaseStream.Seek(commonHeader.CabDescriptorOffset +
					cabDescriptor.FileTableOffset +
					directoryTable[(int)index],
					SeekOrigin.Begin);

			var test = reader.BaseStream.ReadASCIIZ();
			return test;
		}

		public bool Exists(string filename)
		{
			return fileDict.ContainsKey(filename);
		}

		public uint DirectoryCount()
		{
			return cabDescriptor.DirectoryCount;
		}

		public string FileName(uint index)
		{
			if (!fileDescriptors.ContainsKey(index))
				AddFileDescriptorToList(index);
			return fileDescriptors[index].Filename;
		}

		void AddFileDescriptorToList(uint index)
		{
			var reader = new BinaryReader(hdrFile);
			reader.BaseStream.Seek(commonHeader.CabDescriptorOffset +
					cabDescriptor.FileTableOffset +
					cabDescriptor.FileTableOffset2 +
					index * 0x57,
					SeekOrigin.Begin);

			var fd = new FileDescriptor(reader,
				commonHeader.CabDescriptorOffset + cabDescriptor.FileTableOffset);

			fileDescriptors.Add(index, fd);
		}

		public uint FileCount()
		{
			return cabDescriptor.FileCount;
		}

		public void ExtractFile(uint index, string fileName)
		{
			if (!fileDescriptors.ContainsKey(index))
				AddFileDescriptorToList(index);

			var fd = fileDescriptors[index];
			if ((fd.Flags & FileInvalid) != 0) throw new Exception("File Invalid");
			if ((fd.LinkFlags & LinkPrev) != 0)
			{
				ExtractFile(fd.LinkPrevious, fileName);
				return;
			}

			if ((fd.Flags & FileSplit) != 0 || (fd.Flags & FileObfuscated) != 0)
				throw new Exception("Haven't implemented split or obfustcated files");

			var fil    = GlobalFileSystem.Open("{0}{1}.cab".F(commonName, fd.Volume));
			var reader = new BinaryReader(fil);

			if (reader.ReadUInt32() != 0x28635349)
				throw new InvalidDataException("Not an Installshield CAB package");

			reader.BaseStream.Seek(fd.DataOffset, SeekOrigin.Begin);
			var destfile = File.Open(fileName, FileMode.Create);
			var writer   = new BinaryWriter(destfile);

			if ((fd.Flags & FileCompressed) != 0)
			{
				ushort bytesToExtract;
				byte[] readBuffer;
				uint bytesToRead     = fd.CompressedSize;
				const int BufferSize = 65536;

				var writeBuffer = new byte[BufferSize];
				var inf         = new Inflater(true);
				int extractedBytes;

				while (bytesToRead > 0)
				{
					bytesToExtract = reader.ReadUInt16();
					readBuffer     = reader.ReadBytes(bytesToExtract);
					inf.SetInput(readBuffer);
					extractedBytes = inf.Inflate(writeBuffer);

					if (extractedBytes == 0)
						throw new Exception("Inflater Didn't extract Input");

					writer.Write(writeBuffer, 0, extractedBytes);
					bytesToRead -= (uint)bytesToExtract + 2;

					inf.Reset();
				}

				writer.Dispose();
			}
			else
			{
				writer.Write(reader.ReadBytes((int)fd.ExpandedSize));
				writer.Dispose();
			}

			fil.Dispose();
		}

		public void Write(Dictionary<string, byte[]> input)
		{
			throw new NotImplementedException("Cannot Add Files To Cab");
		}

		public IEnumerable<uint> ClassicHashes()
		{
			return fileDict.Keys.Select(k => PackageEntry.HashFilename(k, PackageHashType.Classic));
		}

		public Stream GetContent(string fileName)
		{
			var fileDes = fileDict[fileName];
			if ((fileDes.Flags & FileInvalid) != 0) throw new Exception("File Invalid");
			if ((fileDes.LinkFlags & LinkPrev) != 0)
				throw new NotImplementedException("Link Previous");

			if ((fileDes.Flags & FileSplit) != 0 || (fileDes.Flags & FileObfuscated) != 0)
				throw new NotImplementedException("Haven't implemented split or obfustcated files");

			List<byte> out_array;
			using (var fileStream = GlobalFileSystem.Open("{0}{1}.cab".F(commonName, fileDes.Volume)))
			{
				var reader = new BinaryReader(fileStream);
				if (reader.ReadUInt32() != 0x28635349)
					throw new InvalidDataException("Not an Installshield CAB package");

				fileStream.Seek(fileDes.DataOffset, SeekOrigin.Begin);
				if ((fileDes.Flags & FileCompressed) != 0)
				{
					ushort bytesToExtract;
					long bytesToRead     = (long)fileDes.CompressedSize;
					out_array            = new List<byte>((int)fileDes.ExpandedSize);
					const int BufferSize = 65536;

					byte[] readBuffer;
					int extractedBytes;
					var writeBuffer = new byte[BufferSize];
					var inf         = new Inflater(true);

					while (bytesToRead > 0)
					{
						bytesToExtract = reader.ReadUInt16();
						readBuffer = reader.ReadBytes(bytesToExtract);
						inf.SetInput(readBuffer);
						extractedBytes = inf.Inflate(writeBuffer);

						if (extractedBytes == 0)
							throw new Exception("Inflater Didn't extract Input");

						out_array.AddRange(writeBuffer.Take(extractedBytes).ToArray());
						bytesToRead -= (uint)bytesToExtract + 2;

						inf.Reset();
					}
				}
				else
				{
					var size  = fileDes.ExpandedSize;
					var bytes = new byte[size];

					fileStream.Read(bytes, 0, (int)size);
					out_array = new List<byte>(bytes);
				}
			}

			return new MemoryStream(out_array.ToArray());
		}

		public IEnumerable<uint> CrcHashes()
		{
			yield break;
		}

		public IEnumerable<string> AllFileNames()
		{
			return fileDict.Keys;
		}

		public void Dispose()
		{
			hdrFile.Dispose();
		}
	}
}
